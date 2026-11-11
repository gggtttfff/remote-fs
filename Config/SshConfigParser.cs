using System.Text.RegularExpressions;

namespace RemoteFs.Config;

/// <summary>
/// 从 ~/.ssh/config 里解析出的一条主机配置
/// </summary>
public sealed class SshConfigEntry
{
    /// <summary>Host 声明的别名</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>真实地址</summary>
    public string? HostName { get; set; }

    public int? Port { get; set; }

    public string? User { get; set; }

    /// <summary>私钥路径</summary>
    public string? IdentityFile { get; set; }

    /// <summary>跳板机，本工具暂不支持直连之外的跳板方式</summary>
    public string? ProxyJump { get; set; }

    /// <summary>是否通配符（* 或 ?），通配符不是具体主机，导入时跳过</summary>
    public bool IsWildcard => Pattern.Contains('*') || Pattern.Contains('?');

    /// <summary>实际连接的地址，没有 HostName 时就是别名本身</summary>
    public string ResolveHost() =>
        string.IsNullOrWhiteSpace(HostName) ? Pattern : HostName.Trim();
}

/// <summary>
/// 解析 OpenSSH 的 config 文件。
/// 规则参照 ssh_config：关键字不区分大小写，Host 可以有多个模式，
/// 同一个关键字以最先出现的值为准。
/// </summary>
public static class SshConfigParser
{
    /// <summary>默认的 ssh_config 路径</summary>
    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ssh",
        "config");

    /// <summary>
    /// 解析 ssh_config，返回所有具体主机（不含通配符）
    /// </summary>
    public static List<SshConfigEntry> Parse(string? configPath = null)
    {
        var path = string.IsNullOrWhiteSpace(configPath) ? DefaultConfigPath : configPath;
        var entries = new List<SshConfigEntry>();

        if (!File.Exists(path))
        {
            return entries;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ParseFile(path, entries, visited);
        return entries.Where(entry => !entry.IsWildcard).ToList();
    }

    /// <summary>
    /// 解析单个文件，处理 Include 指令
    /// </summary>
    private static void ParseFile(
        string path, List<SshConfigEntry> entries, HashSet<string> visited)
    {
        var fullPath = Path.GetFullPath(path);
        if (!visited.Add(fullPath))
        {
            // Include 循环引用
            return;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(fullPath);
        }
        catch (Exception)
        {
            return;
        }

        // 当前 Host 块里的关键字取值，先出现者优先
        var blockValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var blockPatterns = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOfAny(new[] { ' ', '\t', '=' });
            if (separatorIndex <= 0)
            {
                continue;
            }

            var keyword = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            // 去掉可能存在的等号分隔
            if (value.StartsWith('='))
            {
                value = value[1..].Trim();
            }

            // 1. 行尾注释要去掉，OpenSSH 里 # 之后到行尾都是注释
            value = StripComment(value);
            if (value.Length == 0)
            {
                continue;
            }

            value = Unquote(value);

            // 1. Include 立即展开，里面的主机优先于后面的
            if (string.Equals(keyword, "Include", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var included in ExpandInclude(value))
                {
                    ParseFile(included, entries, visited);
                }
                continue;
            }

            // 2. Match 块是条件匹配，语义复杂，不导入
            if (string.Equals(keyword, "Match", StringComparison.OrdinalIgnoreCase))
            {
                FlushBlock(entries, blockPatterns, blockValues);
                blockPatterns.Clear();
                blockValues.Clear();
                continue;
            }

            // 3. Host 开启一个新块
            if (string.Equals(keyword, "Host", StringComparison.OrdinalIgnoreCase))
            {
                FlushBlock(entries, blockPatterns, blockValues);
                blockPatterns.Clear();
                blockValues.Clear();

                foreach (var pattern in value.Split(new[] { ' ', '\t' },
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    blockPatterns.Add(pattern);
                }
                continue;
            }

            // 4. 普通关键字，先出现者优先
            if (blockPatterns.Count > 0 && !blockValues.ContainsKey(keyword))
            {
                blockValues[keyword] = value;
            }
        }

        FlushBlock(entries, blockPatterns, blockValues);
    }

    /// <summary>
    /// 把一个 Host 块落成具体的条目
    /// </summary>
    private static void FlushBlock(
        List<SshConfigEntry> entries, List<string> patterns, Dictionary<string, string> values)
    {
        if (patterns.Count == 0)
        {
            return;
        }

        foreach (var pattern in patterns)
        {
            var entry = new SshConfigEntry { Pattern = pattern };
            values.TryGetValue("HostName", out var hostName);
            entry.HostName = hostName;
            values.TryGetValue("User", out var user);
            entry.User = user;
            values.TryGetValue("IdentityFile", out var identityFile);
            entry.IdentityFile = identityFile;
            values.TryGetValue("ProxyJump", out var proxyJump);
            entry.ProxyJump = proxyJump;

            if (int.TryParse(values.GetValueOrDefault("Port"), out var port))
            {
                entry.Port = port;
            }

            entries.Add(entry);
        }
    }

    /// <summary>
    /// 展开 Include 的路径列表，支持通配符
    /// </summary>
    private static IEnumerable<string> ExpandInclude(string value)
    {
        foreach (var rawPath in value.Split(new[] { ' ', '\t' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var expanded = ExpandHome(rawPath);

            // 通配符路径
            if (expanded.Contains('*') || expanded.Contains('?'))
            {
                var directory = Path.GetDirectoryName(expanded);
                var pattern = Path.GetFileName(expanded);
                if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(pattern))
                {
                    continue;
                }

                if (!Directory.Exists(directory))
                {
                    continue;
                }

                IEnumerable<string> matches;
                try
                {
                    matches = Directory.EnumerateFiles(directory, pattern);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var match in matches)
                {
                    yield return match;
                }
                continue;
            }

            if (File.Exists(expanded))
            {
                yield return expanded;
            }
        }
    }

    /// <summary>
    /// 去掉行尾注释。# 前要有空白才算注释，避免误伤路径里的 # 号
    /// </summary>
    private static string StripComment(string value)
    {
        var index = value.IndexOf('#');
        if (index < 0)
        {
            return value;
        }

        // # 前面是空白才认为是注释开头
        if (index > 0 && !char.IsWhiteSpace(value[index - 1]))
        {
            return value;
        }

        return value[..index].Trim();
    }

    /// <summary>
    /// 去掉值外面的引号
    /// </summary>
    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }
        return value;
    }

    /// <summary>
    /// 展开 ~ 与环境变量
    /// </summary>
    private static string ExpandHome(string path)
    {
        var result = Environment.ExpandEnvironmentVariables(path);
        if (result.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            result = Path.Combine(home, result.TrimStart('~', '/', '\\'));
        }
        return result;
    }

    /// <summary>
    /// 检查某条配置是否指向了本机私钥文件
    /// </summary>
    public static bool IdentityFileExists(SshConfigEntry entry)
    {
        var path = ResolveIdentityFile(entry);
        return path is not null && File.Exists(path);
    }

    /// <summary>
    /// 解析出私钥的绝对路径，没有配置返回 null
    /// </summary>
    public static string? ResolveIdentityFile(SshConfigEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.IdentityFile))
        {
            return null;
        }

        var path = ExpandHome(entry.IdentityFile);
        return Path.GetFullPath(path);
    }
}
