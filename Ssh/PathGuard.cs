using RemoteFs.Common;
using RemoteFs.Config;

namespace RemoteFs.Ssh;

/// <summary>
/// 路径白名单校验器：限制可访问的远程路径范围，防止 AI 误操作或提示注入。
/// </summary>
public sealed class PathGuard
{
    private readonly AppConfig _config;

    public PathGuard(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// 校验路径是否在允许范围内。不在范围内则抛出异常。
    /// </summary>
    public void EnsureAllowed(HostProfile profile, string remotePath)
    {
        var normalized = Normalize(remotePath);

        // 强制白名单模式下，未配置根目录的主机一律拒绝
        if (_config.RequireAllowedRoots && profile.AllowedRoots.Count == 0)
        {
            throw new UnauthorizedAccessException(
                $"主机 {profile.Name} 未配置 allowedRoots，且全局启用了强制白名单，已拒绝访问 {normalized}");
        }

        if (profile.AllowedRoots.Count == 0)
        {
            // 未配置白名单时放行，但留痕告警，便于后续收紧
            AuditLogger.Warn($"主机 {profile.Name} 未配置 allowedRoots，放行访问 {normalized}");
            return;
        }

        foreach (var root in profile.AllowedRoots)
        {
            var normalizedRoot = Normalize(root);
            if (normalized == normalizedRoot
                || normalized.StartsWith(normalizedRoot + "/", StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new UnauthorizedAccessException(
            $"路径 {normalized} 不在主机 {profile.Name} 的允许范围内。允许的根目录: {string.Join(", ", profile.AllowedRoots)}");
    }

    /// <summary>
    /// 校验远程命令中引用的路径是否在允许范围内。
    /// 命令执行本身无法完全约束行为，但至少拦住显式引用越界路径的情况，
    /// 避免用 remote_exec 绕过文件工具的路径白名单。
    /// </summary>
    public void EnsureCommandAllowed(HostProfile profile, string command)
    {
        if (profile.AllowedRoots.Count == 0)
        {
            return;
        }

        var referencedPaths = ExtractPaths(command);
        foreach (var referencedPath in referencedPaths)
        {
            var normalized = Normalize(referencedPath);
            var allowed = profile.AllowedRoots.Any(root =>
            {
                var normalizedRoot = Normalize(root);
                return normalized == normalizedRoot
                    || normalized.StartsWith(normalizedRoot + "/", StringComparison.Ordinal);
            });

            if (!allowed)
            {
                throw new UnauthorizedAccessException(
                    $"命令引用了允许范围之外的路径 {normalized}。"
                    + $"主机 {profile.Name} 的允许根目录: {string.Join(", ", profile.AllowedRoots)}");
            }
        }
    }

    /// <summary>
    /// 从命令中提取形如 /path 的绝对路径引用
    /// </summary>
    private static IEnumerable<string> ExtractPaths(string command)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            command, @"(?<![\w/.-])(/[\w./\-*]+)");
        foreach (System.Text.RegularExpressions.Match item in matches)
        {
            yield return item.Value;
        }
    }

    /// <summary>
    /// 规范化远程路径：统一分隔符、解析 . 与 ..、去掉尾部斜杠
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var normalized = path.Replace('\\', '/');
        var isAbsolute = normalized.StartsWith("/");

        var segments = new List<string>();
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                continue;
            }
            segments.Add(segment);
        }

        var joined = string.Join("/", segments);
        if (isAbsolute)
        {
            return "/" + joined;
        }
        return joined.Length == 0 ? "." : joined;
    }

    /// <summary>
    /// 拼接远程路径，自动处理分隔符
    /// </summary>
    public static string Combine(params string[] parts)
    {
        var filtered = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
        if (filtered.Count == 0)
        {
            return "/";
        }

        var first = filtered[0].Replace('\\', '/');
        var combined = first.EndsWith("/") ? first.TrimEnd('/') : first;
        foreach (var part in filtered.Skip(1))
        {
            var segment = part.Replace('\\', '/').Trim('/');
            if (segment.Length == 0)
            {
                continue;
            }
            combined = combined.TrimEnd('/') + "/" + segment;
        }
        return combined.Length == 0 ? "/" : combined;
    }
}
