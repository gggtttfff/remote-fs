using System.Text.RegularExpressions;
using RemoteFs.Common;
using RemoteFs.Config;

namespace RemoteFs.Ssh;

/// <summary>
/// 路径白名单校验器：限制可访问的远程路径范围，防止 AI 误操作或提示注入。
/// </summary>
public sealed class PathGuard
{
    /// <summary>命令中的绝对路径引用。量词用 * 而非 +，单独的 / 也要能命中</summary>
    private static readonly Regex AbsolutePathPattern =
        new(@"(?<![\w/.\-])(/[\w./\-*?~$]*)", RegexOptions.Compiled);

    /// <summary>
    /// 命令中的相对上级目录跳转，无法确定基准目录，一律拒绝。
    /// 前置字符包含 /，否则 $HOME/../etc/passwd 这类写法会漏掉
    /// </summary>
    private static readonly Regex ParentTraversalPattern =
        new(@"(^|[\s=;|&(""'<>/])\.\.(/|\s|$)", RegexOptions.Compiled);

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

        if (!IsUnderAnyRoot(profile, normalized))
        {
            throw new UnauthorizedAccessException(
                $"路径 {normalized} 不在主机 {profile.Name} 的允许范围内。允许的根目录: {string.Join(", ", profile.AllowedRoots)}");
        }
    }

    /// <summary>
    /// 校验远程命令中引用的路径是否在允许范围内。
    /// 只能拦住显式写出绝对路径的写法，变量拼接、命令替换、编码解码都绕得过去，
    /// 需要严格隔离时应把 remoteExecPolicy 设为 Deny。
    /// </summary>
    public void EnsureCommandAllowed(HostProfile profile, string command)
    {
        if (_config.RemoteExecPolicy == RemoteExecPolicy.Deny)
        {
            throw new UnauthorizedAccessException(
                "全局配置 remoteExecPolicy 为 Deny，远程命令执行已被禁用。");
        }

        if (_config.RemoteExecPolicy == RemoteExecPolicy.Allow || profile.AllowedRoots.Count == 0)
        {
            return;
        }

        // 1.1 相对上级目录无法确定基准，直接拒绝
        if (ParentTraversalPattern.IsMatch(command))
        {
            throw new UnauthorizedAccessException(
                $"命令中包含相对上级目录引用（..），无法判断实际访问位置，已拒绝。主机 {profile.Name} 的允许根目录: "
                + string.Join(", ", profile.AllowedRoots));
        }

        // 1.2 逐个校验显式引用的绝对路径
        foreach (Match match in AbsolutePathPattern.Matches(command))
        {
            var normalized = Normalize(match.Value);
            if (!IsUnderAnyRoot(profile, normalized))
            {
                throw new UnauthorizedAccessException(
                    $"命令引用了允许范围之外的路径 {normalized}。"
                    + $"主机 {profile.Name} 的允许根目录: {string.Join(", ", profile.AllowedRoots)}");
            }
        }
    }

    /// <summary>
    /// 判断规范化后的路径是否落在任一允许根目录下
    /// </summary>
    private static bool IsUnderAnyRoot(HostProfile profile, string normalizedPath)
    {
        return profile.AllowedRoots.Any(root =>
        {
            var normalizedRoot = Normalize(root);
            return normalizedPath == normalizedRoot
                || normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// 构造符号链接拒绝提示，供白名单模式下的软链接拦截复用
    /// </summary>
    public static UnauthorizedAccessException BuildSymbolicLinkRejection(HostProfile profile, string path)
    {
        return new UnauthorizedAccessException(
            $"路径 {path} 本身或其某一级上级目录是符号链接。"
            + "白名单模式下为防止借道软链接访问范围外内容，已拒绝该操作。"
            + $"请直接使用链接指向的真实路径，或在 {profile.Name} 的配置里开启 followSymbolicLinks。");
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