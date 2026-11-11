using RemoteFs.Common;
using RemoteFs.Config;

namespace RemoteFs.Services;

/// <summary>
/// 从 ~/.ssh/config 导入主机配置
/// </summary>
public sealed class SshConfigImporter
{
    private readonly ConfigStore _configStore;

    public SshConfigImporter(ConfigStore configStore)
    {
        _configStore = configStore;
    }

    /// <summary>
    /// 扫描 ssh_config 里可导入的主机
    /// </summary>
    public List<SshConfigCandidate> Scan(string? configPath = null)
    {
        var candidates = new List<SshConfigCandidate>();

        foreach (var entry in SshConfigParser.Parse(configPath))
        {
            // 已经存在的同名主机不重复导入
            var existing = _configStore.LoadHosts()
                .FirstOrDefault(host =>
                    string.Equals(host.Name, entry.Pattern, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                continue;
            }

            var identityPath = SshConfigParser.ResolveIdentityFile(entry);

            candidates.Add(new SshConfigCandidate
            {
                Pattern = entry.Pattern,
                Host = entry.ResolveHost(),
                Port = entry.Port ?? 22,
                User = entry.User ?? string.Empty,
                IdentityFile = identityPath,
                IdentityFileExists = identityPath is not null && File.Exists(identityPath),
                ProxyJump = entry.ProxyJump,
                Selected = true
            });
        }

        return candidates;
    }

    /// <summary>
    /// 把选中的候选项导入为正式主机配置
    /// </summary>
    public List<HostProfile> Import(IEnumerable<SshConfigCandidate> candidates)
    {
        var hosts = _configStore.LoadHosts();
        var created = new List<HostProfile>();

        foreach (var candidate in candidates)
        {
            if (!candidate.Selected)
            {
                continue;
            }

            var name = candidate.Pattern;

            // 别名重复时加序号
            var finalName = name;
            var suffix = 2;
            while (hosts.Any(host =>
                       string.Equals(host.Name, finalName, StringComparison.OrdinalIgnoreCase)))
            {
                finalName = $"{name}-{suffix++}";
            }

            var profile = new HostProfile
            {
                Name = finalName,
                Host = candidate.Host,
                Port = candidate.Port,
                Username = candidate.User,
                Auth = AuthMethod.Key,
                PrivateKeyPath = candidate.IdentityFile,
                // 从 ssh_config 导入还没采集指纹，先按不校验处理，采集后再收紧
                StrictHostKeyChecking = false,
                AllowedRoots = new List<string>(),
                CommandTimeoutMilliseconds = 60_000,
                Description = candidate.ProxyJump is { Length: > 0 } proxyJump
                    ? $"来自 ssh_config（ProxyJump: {proxyJump}，本工具不支持跳板，需直连）"
                    : "来自 ssh_config"
            };

            hosts.Add(profile);
            created.Add(profile);
        }

        if (created.Count > 0)
        {
            _configStore.SaveHosts(hosts);
            AuditLogger.Info($"从 ssh_config 导入 {created.Count} 台主机: {string.Join(", ", created.Select(host => host.Name))}");
        }

        return created;
    }
}

/// <summary>
/// 一个待导入的候选项
/// </summary>
public sealed class SshConfigCandidate
{
    public string Pattern { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string User { get; set; } = string.Empty;

    public string? IdentityFile { get; set; }

    public bool IdentityFileExists { get; set; }

    public string? ProxyJump { get; set; }

    /// <summary>是否勾选导入</summary>
    public bool Selected { get; set; } = true;
}
