using RemoteFs.Common;
using RemoteFs.Config;
using RemoteFs.Ssh;
using Renci.SshNet;

namespace RemoteFs.Services;

/// <summary>
/// 远程文件操作服务：封装对远程主机的文件与命令操作，供 MCP 工具层调用。
/// </summary>
public sealed class RemoteFsService
{
    private readonly AppConfig _config;
    private readonly ConfigStore _configStore;
    private readonly SshConnectionFactory _connectionFactory;
    private readonly PathGuard _pathGuard;
    private readonly List<HostProfile> _hostCache;

    public RemoteFsService(AppConfig config, ConfigStore configStore)
    {
        _config = config;
        _configStore = configStore;
        _connectionFactory = new SshConnectionFactory(config);
        _pathGuard = new PathGuard(config);
        _hostCache = configStore.LoadHosts();
    }

    public AppConfig Config => _config;

    /// <summary>配置存储，供工具层查询主机列表</summary>
    public ConfigStore ConfigStore => _configStore;

    /// <summary>
    /// 解析目标主机：未指定时使用默认主机
    /// </summary>
    public HostProfile ResolveHost(string? hostName)
    {
        if (string.IsNullOrWhiteSpace(hostName))
        {
            if (string.IsNullOrWhiteSpace(_config.DefaultHost))
            {
                throw new InvalidOperationException(
                    "未指定主机且未配置 defaultHost。请在工具参数中传入 host，或设置默认主机。");
            }
            return _configStore.RequireHost(_config.DefaultHost, _hostCache);
        }
        return _configStore.RequireHost(hostName, _hostCache);
    }

    /// <summary>
    /// 校验路径白名单
    /// </summary>
    public void EnsurePathAllowed(HostProfile profile, string remotePath)
    {
        _pathGuard.EnsureAllowed(profile, remotePath);
    }

    /// <summary>
    /// 校验命令中引用的路径是否在白名单内，防止用命令绕过文件工具的路径限制
    /// </summary>
    public void EnsureCommandAllowed(HostProfile profile, string command)
    {
        _pathGuard.EnsureCommandAllowed(profile, command);
    }

    /// <summary>
    /// 建立 SFTP 连接，调用方负责释放
    /// </summary>
    public SftpClient OpenSftp(HostProfile profile)
    {
        return _connectionFactory.CreateSftpClient(profile);
    }

    /// <summary>
    /// 建立 SSH 连接，调用方负责释放
    /// </summary>
    public SshClient OpenSsh(HostProfile profile, out string fingerprint)
    {
        return _connectionFactory.CreateSshClient(profile, out fingerprint);
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
}

/// <summary>
/// 命令执行结果
/// </summary>
public sealed record CommandOutput(string StandardOutput, string StandardError, int ExitCode);