using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteFs.Common;

namespace RemoteFs.Config;

/// <summary>
/// 远程命令执行策略
/// </summary>
public enum RemoteExecPolicy
{
    /// <summary>完全禁用 remote_exec</summary>
    Deny,

    /// <summary>放行，但拦截命令中显式引用的白名单外绝对路径（尽力而为，无法防御变量拼接等写法）</summary>
    Guard,

    /// <summary>完全放行，不做路径检查</summary>
    Allow
}

/// <summary>
/// 全局配置
/// </summary>
public sealed class AppConfig
{
    /// <summary>默认主机，未指定时使用</summary>
    public string? DefaultHost { get; set; }

    /// <summary>是否强制所有主机配置路径白名单（开启后 AllowedRoots 为空的主机无法访问任何路径）</summary>
    public bool RequireAllowedRoots { get; set; }

    /// <summary>单个文件读取的最大字节数</summary>
    public int MaxReadBytes { get; set; } = 1_048_576;

    /// <summary>单次工具输出的最大字符数</summary>
    public int MaxOutputCharacters { get; set; } = 200_000;

    /// <summary>目录列表最大条目数</summary>
    public int MaxListEntries { get; set; } = 2_000;

    /// <summary>目录树最大深度</summary>
    public int MaxTreeDepth { get; set; } = 4;

    /// <summary>搜索结果最大条数</summary>
    public int MaxSearchResults { get; set; } = 200;

    /// <summary>是否记录审计日志</summary>
    public bool AuditLogEnabled { get; set; } = true;

    /// <summary>日志等级，低于该等级的不写入</summary>
    public Logging.LogLevel LogLevel { get; set; } = Logging.LogLevel.Info;

    /// <summary>是否记录工具调用的完整出入参。开启后服务器上的文件内容会明文落盘</summary>
    public bool LogRecordPayloads { get; set; } = true;

    /// <summary>
    /// 是否额外记录每次 MCP 工具调用的耗时。
    /// 开启后每次调用再写一条 tool_timing，只含工具名、主机、耗时和成败，不含文件内容。
    /// </summary>
    public bool LogTraceToolTiming { get; set; }

    /// <summary>单个日志文件的大小上限（字节），超过后自动分割</summary>
    public long LogMaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>日志保留天数，超过后自动清理</summary>
    public int LogRetentionDays { get; set; } = 7;

    /// <summary>最多保留的日志文件数</summary>
    public int LogMaxFiles { get; set; } = 20;

    /// <summary>SSH 连接超时（毫秒）</summary>
    public int ConnectionTimeoutMilliseconds { get; set; } = 30_000;

    /// <summary>SFTP 单次操作超时（毫秒），避免远端无响应时永久卡住</summary>
    public int SftpOperationTimeoutMilliseconds { get; set; } = 60_000;

    /// <summary>SFTP 连接空闲多久后回收（秒）</summary>
    public int ConnectionIdleSeconds { get; set; } = 300;

    /// <summary>remote_exec 的路径防护策略</summary>
    public RemoteExecPolicy RemoteExecPolicy { get; set; } = RemoteExecPolicy.Guard;

    /// <summary>是否允许操作符号链接。默认拒绝，防止借道软链接跳出白名单</summary>
    public bool FollowSymbolicLinks { get; set; }

    /// <summary>是否启用远程目录枚举缓存</summary>
    public bool CacheEnabled { get; set; } = true;

    /// <summary>目录枚举缓存的有效期（秒），超时后重新枚举</summary>
    public int CacheTtlSeconds { get; set; } = 30;

    /// <summary>默认文本编码（主机未单独指定时生效）。空值按 utf-8 处理</summary>
    public string? DefaultEncoding { get; set; } = "utf-8";

    /// <summary>后台任务的最长运行时间（分钟），到期由远程看护进程强制终止</summary>
    public int BackgroundJobTtlMinutes { get; set; } = 30;

    /// <summary>远程任务目录的保留天数，超期且已结束的任务会被清理</summary>
    public int JobRetentionDays { get; set; } = 7;

    /// <summary>每台主机最多保留的已结束任务数，超出后按时间从旧到新清理</summary>
    public int JobMaxCount { get; set; } = 50;

    /// <summary>关闭主窗口时是否驻留后台（托盘），而不是退出进程</summary>
    public bool StayInBackgroundOnExit { get; set; }

    /// <summary>备用通道监听端口。0 表示不在配置里指定，listen 命令默认 43336。</summary>
    public int AgentListenPort { get; set; }

    /// <summary>验签用的公钥文件。空则使用运行用户的 ~/.ssh/authorized_keys。</summary>
    public string? AgentAuthorizedKeysPath { get; set; }

    /// <summary>备用通道允许修改的本机目录。为空时拒绝启动监听。</summary>
    public List<string> AgentAllowedRoots
    {
        get => _agentAllowedRoots;
        set => _agentAllowedRoots = value ?? new List<string>();
    }

    private List<string> _agentAllowedRoots = new();

    /// <summary>主机配置文件路径</summary>
    public string? HostsFilePath { get; set; }
}

/// <summary>
/// 配置存储，负责 hosts.json 与 config.json 的读写
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _hostsFilePath;
    private readonly string _configFilePath;

    public ConfigStore(string? hostsFilePath = null)
    {
        _hostsFilePath = hostsFilePath ?? Path.Combine(ConfigDirectory, "hosts.json");
        _configFilePath = Path.Combine(ConfigDirectory, "config.json");
    }

    /// <summary>
    /// 配置根目录：程序所在目录下的 config，可用 REMOTE_FS_HOME 环境变量覆盖
    /// </summary>
    public static string ConfigDirectory
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("REMOTE_FS_HOME");
            if (!string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }
            return Path.Combine(InstallRoot, "config");
        }
    }

    /// <summary>
    /// 缓存根目录：程序所在目录下的 cache
    /// </summary>
    public static string CacheDirectory => Path.Combine(InstallRoot, "cache");

    /// <summary>
    /// 程序安装目录（单文件发布下就是 exe 所在目录）
    /// </summary>
    public static string InstallRoot => AppContext.BaseDirectory;

    /// <summary>
    /// 旧版本的配置目录，用于一次性迁移
    /// </summary>
    private static string LegacyConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".remote-fs");

    /// <summary>
    /// 准备配置目录。首次运行时把旧目录里的配置复制过来，原件保留不删。
    /// </summary>
    public static void EnsureInitialized()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            // 缓存目录跟着配置目录一起准备，任何子命令启动都会有
            Cache.CacheStore.EnsureDirectories();
        }
        catch (Exception exception)
        {
            AuditLogger.Warn($"无法创建配置目录 {ConfigDirectory}: {exception.Message}");
            return;
        }

        MigrateLegacyConfig();
    }

    /// <summary>
    /// 把 ~/.remote-fs 下的旧配置复制到新的 config 目录，只在新目录还没有对应文件时复制
    /// </summary>
    private static void MigrateLegacyConfig()
    {
        // 指定了 REMOTE_FS_HOME 说明是显式指定位置，不做迁移
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REMOTE_FS_HOME")))
        {
            return;
        }

        var legacyDirectory = LegacyConfigDirectory;
        if (!Directory.Exists(legacyDirectory)
            || string.Equals(
                Path.GetFullPath(legacyDirectory), Path.GetFullPath(ConfigDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var fileName in new[] { "hosts.json", "config.json" })
        {
            var legacyPath = Path.Combine(legacyDirectory, fileName);
            var targetPath = Path.Combine(ConfigDirectory, fileName);

            if (!File.Exists(legacyPath) || File.Exists(targetPath))
            {
                continue;
            }

            try
            {
                File.Copy(legacyPath, targetPath);
                AuditLogger.Info(
                    $"已把旧配置 {legacyPath} 复制到 {targetPath}，旧文件保留未删除");
            }
            catch (Exception exception)
            {
                AuditLogger.Warn($"迁移旧配置 {legacyPath} 失败: {exception.Message}");
            }
        }
    }

    public string HostsFilePath => _hostsFilePath;

    /// <summary>加载全局配置，文件不存在时返回默认配置</summary>
    public AppConfig LoadConfig()
    {
        if (!File.Exists(_configFilePath))
        {
            return new AppConfig();
        }
        try
        {
            var json = File.ReadAllText(_configFilePath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            return config ?? new AppConfig();
        }
        catch (Exception exception)
        {
            // 配置损坏时兜底为默认配置，不阻断服务启动
            AuditLogger.Warn($"读取全局配置失败，已回退默认配置: {exception.Message}");
            return new AppConfig();
        }
    }

    public void SaveConfig(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        AtomicFileWriter.WriteAllText(_configFilePath, json);
    }

    /// <summary>加载全部主机配置</summary>
    public List<HostProfile> LoadHosts()
    {
        if (!File.Exists(_hostsFilePath))
        {
            return new List<HostProfile>();
        }
        try
        {
            var json = File.ReadAllText(_hostsFilePath);
            var hosts = JsonSerializer.Deserialize<List<HostProfile>>(json, JsonOptions);
            return hosts ?? new List<HostProfile>();
        }
        catch (Exception exception)
        {
            AuditLogger.Error($"读取主机配置失败: {exception.Message}");
            throw new InvalidOperationException($"主机配置文件 {_hostsFilePath} 解析失败: {exception.Message}", exception);
        }
    }

    public void SaveHosts(List<HostProfile> hosts)
    {
        var json = JsonSerializer.Serialize(hosts, JsonOptions);
        AtomicFileWriter.WriteAllText(_hostsFilePath, json);
        WarnPlaintextPasswords(hosts);
    }

    /// <summary>
    /// 明文密码会随配置文件一起备份、同步、截图，出现即告警
    /// </summary>
    private void WarnPlaintextPasswords(List<HostProfile> hosts)
    {
        var plaintextHosts = hosts
            .Where(host => !string.IsNullOrEmpty(host.Password))
            .Select(host => host.Name)
            .ToList();
        if (plaintextHosts.Count > 0)
        {
            AuditLogger.Warn(
                $"以下主机的登录密码以明文保存在 {_hostsFilePath}：{string.Join(", ", plaintextHosts)}。"
                + "建议改用 passwordEnvironmentVariable 从环境变量读取。");
        }
    }

    /// <summary>按别名查找主机，找不到时抛出可读异常</summary>
    public HostProfile RequireHost(string name, List<HostProfile>? cache = null)
    {
        var hosts = cache ?? LoadHosts();
        var host = hosts.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (host is null)
        {
            var available = hosts.Count == 0
                ? "（尚未配置任何主机）"
                : string.Join(", ", hosts.Select(item => item.Name));
            throw new KeyNotFoundException($"未找到主机 '{name}'。已配置主机: {available}");
        }
        return host;
    }
}