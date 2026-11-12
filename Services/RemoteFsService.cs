using RemoteFs.Common;
using RemoteFs.Config;
using RemoteFs.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RemoteFs.Services;

/// <summary>
/// 远程文件操作服务：封装对远程主机的文件与命令操作，供 MCP 工具层调用。
/// </summary>
public sealed class RemoteFsService : IDisposable
{
    private readonly AppConfig _config;
    private readonly ConfigStore _configStore;
    private readonly SshConnectionFactory _connectionFactory;
    private readonly SftpConnectionPool _sftpPool;
    private readonly PathGuard _pathGuard;
    private readonly Cache.CacheStore _cacheStore;
    private readonly List<HostProfile> _hostCache;
    private readonly RemoteJobManager _jobManager;

    /// <summary>hosts.json 的变更戳（修改时间与长度混合）。变了就重读配置并丢弃旧连接</summary>
    private long _hostsFileStamp;

    public RemoteFsService(AppConfig config, ConfigStore configStore)
    {
        _config = config;
        _configStore = configStore;
        _connectionFactory = new SshConnectionFactory(config);
        _sftpPool = new SftpConnectionPool(_connectionFactory, config);
        _pathGuard = new PathGuard(config);
        _cacheStore = new Cache.CacheStore(config);
        _cacheStore.Initialize();
        _hostCache = configStore.LoadHosts();
        _hostsFileStamp = ReadHostsFileStamp();
        _jobManager = new RemoteJobManager(config, _connectionFactory, _sftpPool);
    }

    public AppConfig Config => _config;

    /// <summary>远程任务管理器，供命令执行类工具使用</summary>
    public RemoteJobManager Jobs => _jobManager;

    /// <summary>缓存存储，供工具层使用</summary>
    public Cache.CacheStore Cache => _cacheStore;

    /// <summary>
    /// 某台主机发生写操作后，让它的目录枚举缓存失效，避免读到过期结构
    /// </summary>
    public void InvalidateCache(HostProfile profile)
    {
        var removed = _cacheStore.InvalidateHost(profile.Name);
        if (removed > 0)
        {
            AuditLogger.Debug($"已失效主机 {profile.Name} 的 {removed} 条目录缓存");
        }
    }

    /// <summary>配置存储，供工具层查询主机列表</summary>
    public ConfigStore ConfigStore => _configStore;

    /// <summary>
    /// 解析目标主机：未指定时使用默认主机
    /// </summary>
    public HostProfile ResolveHost(string? hostName)
    {
        // 1.1 配置文件改过就先重读：新增、删除、改连接参数都要立刻生效。
        // MCP 服务是常驻进程，不能要求用户重启客户端
        var justReloaded = RefreshHostCacheIfChanged();

        var name = ResolveHostName(hostName);

        // 1.2 再看缓存；刚才没重读过而这里又没命中，才需要再读一次兜底
        try
        {
            return _configStore.RequireHost(name, _hostCache);
        }
        catch (KeyNotFoundException)
        {
            if (!justReloaded)
            {
                RefreshHostCache();
            }
            return _configStore.RequireHost(name, _hostCache);
        }
    }

    /// <summary>
    /// 读 hosts.json 的变更戳：修改时间与长度混合，任一变化都算配置变了
    /// </summary>
    private long ReadHostsFileStamp()
    {
        try
        {
            var info = new FileInfo(_configStore.HostsFilePath);
            return info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0;
        }
        catch (Exception exception)
        {
            // 读不到状态就返回 -1，调用方沿用现有缓存，不要让所有工具一起不可用
            AuditLogger.Warn($"读取主机配置文件状态失败: {exception.Message}");
            return -1;
        }
    }

    /// <summary>
    /// 配置文件变了才重读，避免每次工具调用都读盘。
    /// 返回是否真的重读过，调用方据此决定还要不要兜底重读
    /// </summary>
    private bool RefreshHostCacheIfChanged()
    {
        var stamp = ReadHostsFileStamp();
        if (stamp < 0 || stamp == _hostsFileStamp)
        {
            return false;
        }

        if (!RefreshHostCache())
        {
            return false;
        }

        _hostsFileStamp = stamp;
        return true;
    }

    /// <summary>
    /// 解析目标主机名：未指定时回落到默认主机
    /// </summary>
    private string ResolveHostName(string? hostName)
    {
        if (!string.IsNullOrWhiteSpace(hostName))
        {
            return hostName;
        }

        if (string.IsNullOrWhiteSpace(_config.DefaultHost))
        {
            throw new InvalidOperationException(
                "未指定主机且未配置 defaultHost。请在工具参数中传入 host，或设置默认主机。");
        }
        return _config.DefaultHost;
    }

    /// <summary>
    /// 重新读取主机配置文件
    /// </summary>
    private bool RefreshHostCache()
    {
        try
        {
            var reloaded = _configStore.LoadHosts();
            _hostCache.Clear();
            _hostCache.AddRange(reloaded);

            // 配置变了：被删除的主机不能靠池里的活连接继续用，连接参数改过的也不能沿用旧连接
            var dropped = _sftpPool.InvalidateAll();
            AuditLogger.Info(
                $"主机配置已变更，重新读取到 {reloaded.Count} 台主机，丢弃 {dropped} 条旧连接");
            return true;
        }
        catch (Exception exception)
        {
            // 配置文件损坏时保留旧缓存，不要让所有工具一起不可用
            AuditLogger.Warn(
                $"重新读取主机配置失败，继续使用内存中的 {_hostCache.Count} 台主机: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 白名单校验。不依赖连接，越界路径在建立任何远程连接之前就会被拒绝。
    /// </summary>
    public void EnsurePathAllowed(HostProfile profile, string remotePath)
    {
        _pathGuard.EnsureAllowed(profile, remotePath);
    }

    /// <summary>
    /// 白名单生效时拒绝符号链接：软链接的路径字符串本身合规，指向的可能是范围外内容。
    /// 需要已建立的 SFTP 连接，因此在 EnsurePathAllowed 之后调用。
    /// </summary>
    public void EnsureNotSymbolicLink(HostProfile profile, SftpClient sftp, string remotePath)
    {
        if (_config.FollowSymbolicLinks || profile.AllowedRoots.Count == 0)
        {
            return;
        }

        if (RemotePathInspector.IsSymbolicLink(sftp, remotePath))
        {
            throw PathGuard.BuildSymbolicLinkRejection(profile, remotePath);
        }
    }

    /// <summary>
    /// 校验命令中引用的路径是否在白名单内，防止用命令绕过文件工具的路径限制
    /// </summary>
    public void EnsureCommandAllowed(HostProfile profile, string command)
    {
        _pathGuard.EnsureCommandAllowed(profile, command);
    }

    /// <summary>
    /// 取出一条 SFTP 连接，调用方 using 释放即可，底层连接会被连接池复用。
    /// 连接阶段失败时抛出 <see cref="Agent.SshUnavailableException"/>，供备用端口接手。
    /// </summary>
    public SftpLease OpenSftp(HostProfile profile)
    {
        try
        {
            return _sftpPool.Acquire(profile);
        }
        catch (Exception exception) when (exception is not Agent.SshUnavailableException
                                         && Agent.SshUnavailableException.IsTransportDown(exception))
        {
            throw new Agent.SshUnavailableException(profile, exception);
        }
    }

    /// <summary>
    /// 建立 SSH 连接，调用方负责释放。
    /// 连接阶段失败时抛出 <see cref="Agent.SshUnavailableException"/>。
    /// </summary>
    public SshClient OpenSsh(HostProfile profile, out string fingerprint)
    {
        try
        {
            return _connectionFactory.CreateSshClient(profile, out fingerprint);
        }
        catch (Exception exception) when (exception is not Agent.SshUnavailableException
                                         && Agent.SshUnavailableException.IsTransportDown(exception))
        {
            fingerprint = string.Empty;
            throw new Agent.SshUnavailableException(profile, exception);
        }
    }

    /// <summary>
    /// 执行远程命令并返回输出
    /// </summary>
    public CommandOutput ExecuteCommand(HostProfile profile, string command, int? timeoutMilliseconds)
    {
        using var client = OpenSsh(profile, out _);
        var timeout = timeoutMilliseconds ?? profile.CommandTimeoutMilliseconds;

        using var commandRunner = client.CreateCommand(command);
        commandRunner.CommandTimeout = TimeSpan.FromMilliseconds(timeout);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeout));
        try
        {
            commandRunner.ExecuteAsync(cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"命令执行超过 {timeout} 毫秒已中止: {command}");
        }

        var output = commandRunner.Result ?? string.Empty;
        var error = commandRunner.Error ?? string.Empty;
        var exitCode = commandRunner.ExitStatus ?? -1;

        AuditLogger.Info($"命令执行 [{profile.Name}] exit={exitCode}: {command}");
        return new CommandOutput(output, error, exitCode);
    }

    /// <summary>
    /// 解析本次操作使用的文本编码。
    /// 优先级：工具参数 → 主机配置 → 全局配置 → auto。
    /// </summary>
    /// <param name="profile">目标主机</param>
    /// <param name="explicitName">工具调用里显式指定的编码名，可为空</param>
    public string ResolveEncodingName(HostProfile profile, string? explicitName)
    {
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return TextEncodingResolver.Normalize(explicitName);
        }

        if (!string.IsNullOrWhiteSpace(profile.DefaultEncoding))
        {
            return TextEncodingResolver.Normalize(profile.DefaultEncoding);
        }

        if (!string.IsNullOrWhiteSpace(_config.DefaultEncoding))
        {
            return TextEncodingResolver.Normalize(_config.DefaultEncoding);
        }

        return TextEncodingResolver.Auto;
    }

    /// <summary>
    /// 校验编码名是否受支持。写路径必须显式校验：
    /// 写入用的编码解析对未知名是静默回退 UTF-8 的，不校验会把编码写错还不报错
    /// </summary>
    public static string? ValidateEncodingName(string encodingName)
    {
        if (TextEncodingResolver.IsSupported(encodingName))
        {
            return null;
        }
        return $"不支持的编码 \"{encodingName}\"。可用值："
            + string.Join("、", TextEncodingResolver.SupportedNames);
    }

    /// <summary>
    /// 覆盖写之后把原来的权限与属主补回去。
    /// 原子写是「临时文件 + rename」，新文件的权限和属主来自临时文件，
    /// 不补的话 600 的私钥会变成 644、别的用户拥有的文件会变成当前用户拥有
    /// </summary>
    public void RestoreFileAttributes(
        HostProfile profile, SftpClient sftp, string remotePath, FileAttributesSnapshot? original)
    {
        if (original is null)
        {
            return;
        }

        try
        {
            var command = RemoteFileAttributes.BuildRestoreCommand(sftp, remotePath, original);
            if (command is null)
            {
                return;
            }

            var output = ExecuteCommand(profile, command, 30_000);
            if (output.ExitCode != 0)
            {
                AuditLogger.Warn(
                    $"恢复文件属性失败 [{profile.Name}] {remotePath}: {output.StandardError.Trim()}");
                return;
            }

            AuditLogger.Info(
                $"已恢复文件属性 [{profile.Name}] {remotePath}"
                + $"（{Convert.ToString(original.Mode, 8).PadLeft(4, '0')} {original.UserId}:{original.GroupId}）");
        }
        catch (Exception exception)
        {
            // 文件内容已经写好，属性恢复失败只能留痕，不能把写入算成失败
            AuditLogger.Warn($"恢复文件属性异常 [{profile.Name}] {remotePath}: {exception.Message}");
        }
    }

    /// <summary>
    /// 截断过长的输出，并追加提示
    /// </summary>
    public string TruncateOutput(string text)
    {
        if (text.Length <= _config.MaxOutputCharacters)
        {
            return text;
        }
        var truncated = text[.._config.MaxOutputCharacters];
        return truncated
            + $"\n\n[输出已截断，共 {text.Length} 字符，仅返回前 {_config.MaxOutputCharacters} 字符。"
            + "可缩小范围后重新获取。]";
    }

    /// <summary>
    /// 把请求的条目上限收敛到全局配置允许的范围
    /// </summary>
    public int ClampResultCount(int requested)
    {
        return Math.Clamp(requested, 1, _config.MaxSearchResults);
    }

    /// <summary>
    /// 把请求的目录深度收敛到全局配置允许的范围
    /// </summary>
    public int ClampTreeDepth(int requested)
    {
        return Math.Clamp(requested, 1, Math.Max(1, _config.MaxTreeDepth));
    }

    /// <summary>
    /// 格式化字节数为可读文本
    /// </summary>
    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return unitIndex == 0 ? $"{bytes} B" : $"{size:F1} {units[unitIndex]}";
    }

    public void Dispose()
    {
        // 先终止任务再关闭连接池：清理阶段还要连远程下发终止信号
        _jobManager.KillAllStarted();
        _jobManager.Dispose();
        _sftpPool.Dispose();
    }
}

/// <summary>
/// 命令执行结果
/// </summary>
public sealed record CommandOutput(string StandardOutput, string StandardError, int ExitCode);