using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteFs.Common;

namespace RemoteFs.Config;

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

    /// <summary>配置根目录，可用 REMOTE_FS_HOME 环境变量覆盖</summary>
    public static string ConfigDirectory
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("REMOTE_FS_HOME");
            if (!string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".remote-fs");
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
