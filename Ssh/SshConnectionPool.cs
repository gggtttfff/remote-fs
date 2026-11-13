using RemoteFs.Common;
using RemoteFs.Config;
using Renci.SshNet;

namespace RemoteFs.Ssh;

/// <summary>
/// SSH 连接池：按主机复用已建立的会话。
/// SSH.NET 的 CreateCommand 是在已有 Session 上开新通道，一条 TCP 连接可以连续跑多条命令，
/// 所以复用会话之后，remote_exec 与属性恢复不再每次都付一次完整握手。
/// </summary>
public sealed class SshConnectionPool : IDisposable
{
    private readonly SshConnectionFactory _factory;
    private readonly AppConfig _config;
    private readonly Dictionary<string, PooledSsh> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();
    private bool _disposed;

    public SshConnectionPool(SshConnectionFactory factory, AppConfig config)
    {
        _factory = factory;
        _config = config;
    }

    /// <summary>
    /// 取出一条可用会话，调用方通过返回的租约释放
    /// </summary>
    public SshLease Acquire(HostProfile profile, out string fingerprint)
    {
        // 已经知道该走备用端口时不再尝试 SSH
        Agent.SshRouteCache.ThrowIfSkipped(profile);

        // 1.1 命中已有连接：锁内只查表，不做握手
        lock (_syncRoot)
        {
            ThrowIfDisposed();

            if (_connections.TryGetValue(profile.Name, out var existing) && existing.IsUsable(_config.ConnectionIdleSeconds))
            {
                existing.Touch();
                fingerprint = existing.Fingerprint;
                return new SshLease(this, profile.Name, existing.Client);
            }

            if (existing is not null)
            {
                DisposeEntry(profile.Name, existing);
            }
        }

        // 1.2 握手在锁外做。握手要几秒，放在锁里会让其它主机全部排队等它
        AuditLogger.Info($"建立 SSH 连接 [{profile.Name}]");
        var client = _factory.CreateSshClient(profile, out var actualFingerprint);

        lock (_syncRoot)
        {
            if (_disposed)
            {
                client.Dispose();
                throw new ObjectDisposedException(nameof(SshConnectionPool));
            }

            // 1.3 并发时可能已有别的线程建好了同一条连接，多的这份直接丢弃
            if (_connections.TryGetValue(profile.Name, out var raced) && raced.IsUsable(_config.ConnectionIdleSeconds))
            {
                client.Dispose();
                raced.Touch();
                fingerprint = raced.Fingerprint;
                return new SshLease(this, profile.Name, raced.Client);
            }

            _connections[profile.Name] = new PooledSsh(client, actualFingerprint);
            fingerprint = actualFingerprint;
            return new SshLease(this, profile.Name, client);
        }
    }

    /// <summary>
    /// 归还连接，更新空闲时间
    /// </summary>
    internal void Release(string hostName)
    {
        lock (_syncRoot)
        {
            if (_connections.TryGetValue(hostName, out var entry))
            {
                entry.Touch();
            }
        }
    }

    /// <summary>
    /// 连接已损坏，丢弃后下次重新建立
    /// </summary>
    public void Invalidate(string hostName)
    {
        lock (_syncRoot)
        {
            if (_connections.TryGetValue(hostName, out var entry))
            {
                DisposeEntry(hostName, entry);
                AuditLogger.Warn($"已丢弃主机 {hostName} 的 SSH 连接，下次调用将重新建立");
            }
        }
    }

    /// <summary>
    /// 丢弃全部连接。主机配置变更后调用：
    /// 被删除的主机不能靠池里的活连接继续用，连接参数改过的主机也不能沿用旧连接
    /// </summary>
    public int InvalidateAll()
    {
        lock (_syncRoot)
        {
            var count = _connections.Count;
            foreach (var hostName in _connections.Keys.ToList())
            {
                DisposeEntry(hostName, _connections[hostName]);
            }
            return count;
        }
    }

    private void DisposeEntry(string hostName, PooledSsh entry)
    {
        _connections.Remove(hostName);
        try
        {
            entry.Client.Dispose();
        }
        catch (Exception exception)
        {
            // 释放失败不影响后续重连
            AuditLogger.Warn($"释放主机 {hostName} 的 SSH 连接失败: {exception.Message}");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SshConnectionPool));
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var hostName in _connections.Keys.ToList())
            {
                DisposeEntry(hostName, _connections[hostName]);
            }
            _connections.Clear();
        }
    }

    /// <summary>
    /// 池中保存的会话
    /// </summary>
    private sealed class PooledSsh
    {
        public PooledSsh(SshClient client, string fingerprint)
        {
            Client = client;
            Fingerprint = fingerprint;
            LastUsedUtc = DateTime.UtcNow;
        }

        public SshClient Client { get; }

        /// <summary>握手时拿到的指纹，复用连接时一并返回给调用方</summary>
        public string Fingerprint { get; }

        public DateTime LastUsedUtc { get; private set; }

        /// <summary>
        /// 是否仍可用：底层会话连通且未超过空闲回收时间
        /// </summary>
        public bool IsUsable(int idleSeconds)
        {
            return Client.IsConnected
                && (DateTime.UtcNow - LastUsedUtc).TotalSeconds < idleSeconds;
        }

        public void Touch() => LastUsedUtc = DateTime.UtcNow;
    }
}

/// <summary>
/// SSH 会话租约：Dispose 时把会话交回池，发现连接已断则触发回收
/// </summary>
public sealed class SshLease : IDisposable
{
    private readonly SshConnectionPool _pool;
    private readonly string _hostName;
    private bool _disposed;

    internal SshLease(SshConnectionPool pool, string hostName, SshClient client)
    {
        _pool = pool;
        _hostName = hostName;
        Client = client;
    }

    public SshClient Client { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (Client.IsConnected)
        {
            _pool.Release(_hostName);
        }
        else
        {
            _pool.Invalidate(_hostName);
        }
    }
}
