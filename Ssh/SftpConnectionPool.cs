using RemoteFs.Common;
using RemoteFs.Config;
using Renci.SshNet;

namespace RemoteFs.Ssh;

/// <summary>
/// SFTP 连接池：按主机复用已建立的连接，避免每次工具调用都重新握手。
/// MCP 工具调用是串行处理的，因此单个 SftpClient 实例不会被并发访问。
/// </summary>
public sealed class SftpConnectionPool : IDisposable
{
    private readonly SshConnectionFactory _factory;
    private readonly AppConfig _config;
    private readonly Dictionary<string, PooledConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();
    private bool _disposed;

    public SftpConnectionPool(SshConnectionFactory factory, AppConfig config)
    {
        _factory = factory;
        _config = config;
    }

    /// <summary>
    /// 取出一条可用连接，调用方通过返回的租约释放
    /// </summary>
    public SftpLease Acquire(HostProfile profile)
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();

            // 1.1 复用空闲期内且仍然连通的连接
            if (_connections.TryGetValue(profile.Name, out var existing) && existing.IsUsable(_config.ConnectionIdleSeconds))
            {
                existing.Touch();
                return new SftpLease(this, profile.Name, existing.Client);
            }

            // 1.2 旧连接不可用则先回收再重建
            if (existing is not null)
            {
                DisposeEntry(profile.Name, existing);
            }

            AuditLogger.Info($"建立 SFTP 连接 [{profile.Name}]");
            var client = _factory.CreateSftpClient(profile);
            _connections[profile.Name] = new PooledConnection(client);
            return new SftpLease(this, profile.Name, client);
        }
    }

    /// <summary>
    /// 归还连接
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
                AuditLogger.Warn($"已丢弃主机 {hostName} 的 SFTP 连接，下次调用将重新建立");
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

    private void DisposeEntry(string hostName, PooledConnection entry)
    {
        _connections.Remove(hostName);
        try
        {
            entry.Client.Dispose();
        }
        catch (Exception exception)
        {
            // 释放失败不影响后续重连
            AuditLogger.Warn($"释放主机 {hostName} 的 SFTP 连接失败: {exception.Message}");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SftpConnectionPool));
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
    /// 池中保存的连接
    /// </summary>
    private sealed class PooledConnection
    {
        public PooledConnection(SftpClient client)
        {
            Client = client;
            LastUsedUtc = DateTime.UtcNow;
        }

        public SftpClient Client { get; }

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
/// SFTP 连接租约：Dispose 时把连接交回池，发现连接已断则触发回收
/// </summary>
public sealed class SftpLease : IDisposable
{
    private readonly SftpConnectionPool _pool;
    private readonly string _hostName;
    private bool _disposed;

    internal SftpLease(SftpConnectionPool pool, string hostName, SftpClient client)
    {
        _pool = pool;
        _hostName = hostName;
        Client = client;
    }

    public SftpClient Client { get; }

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