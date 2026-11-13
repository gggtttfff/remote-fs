namespace RemoteFs.Common;

using RemoteFs.Config;

/// <summary>
/// 本地路径白名单，限制上传下载能触碰的本机目录。
/// 远程侧有 allowedRoots，本机侧同样需要一个边界：
/// 否则提示注入可以借 remote_upload 把 ~/.ssh 里的私钥传出去，
/// 或借 remote_download 覆盖任意本地文件。
/// </summary>
public static class LocalPathGuard
{
    /// <summary>
    /// 校验本地路径是否落在允许目录内。未配置 localRoots 时放行，但留痕告警。
    /// </summary>
    public static void EnsureAllowed(AppConfig config, string localPath)
    {
        if (config.LocalRoots.Count == 0)
        {
            AuditLogger.Warn($"未配置 localRoots，放行本地路径 {localPath}");
            return;
        }

        var fullPath = Path.GetFullPath(localPath);
        if (config.LocalRoots.Any(root => IsUnderRoot(fullPath, root)))
        {
            return;
        }

        throw new UnauthorizedAccessException(
            $"本地路径 {fullPath} 不在允许范围内。允许的根目录: {string.Join(", ", config.LocalRoots)}。"
            + "需要放行时修改全局配置的 localRoots。");
    }

    /// <summary>
    /// 判断绝对路径是否落在某个根目录下。按目录边界比较，
    /// 避免 /home/user2 被 /home/user 命中
    /// </summary>
    private static bool IsUnderRoot(string fullPath, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            // 根目录本身写错了，按不匹配处理，不要让整个上传下载不可用
            AuditLogger.Warn($"localRoots 中的路径无法解析，已忽略: {root}");
            return false;
        }

        if (fullRoot.Length == 0)
        {
            return false;
        }

        if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}