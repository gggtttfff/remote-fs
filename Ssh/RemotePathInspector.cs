using Renci.SshNet;
using Renci.SshNet.Common;

namespace RemoteFs.Ssh;

/// <summary>
/// 远程路径属性探测。
/// SftpClient.GetAttributes 走的是 STAT 语义（会跟随软链接），拿不到链接本身的信息，
/// 因此判断「是不是软链接」只能靠目录列出（READDIR 返回的是 lstat 数据）。
/// </summary>
public static class RemotePathInspector
{
    /// <summary>
    /// 判断路径本身是否为符号链接
    /// </summary>
    public static bool IsSymbolicLink(SftpClient sftp, string remotePath)
    {
        var normalized = PathGuard.Normalize(remotePath);
        if (normalized is "/" or ".")
        {
            return false;
        }

        var lastSlash = normalized.LastIndexOf('/');
        var fileName = lastSlash < 0 ? normalized : normalized[(lastSlash + 1)..];
        var parentDirectory = lastSlash <= 0 ? "/" : normalized[..lastSlash];

        try
        {
            return sftp.ListDirectory(parentDirectory).Any(entry =>
                string.Equals(entry.Name, fileName, StringComparison.Ordinal) && entry.IsSymbolicLink);
        }
        catch (SftpPathNotFoundException)
        {
            // 父目录都不存在，交给后续操作报错
            return false;
        }
    }

    /// <summary>
    /// 判断路径上任意一级分量是否为符号链接（含最后一段）。
    /// 只查最后一段时，白名单是 /data 而 /data/link 指向 /etc 的情况下，
    /// /data/link/passwd 的每一级字符串都合规，实际却读到了范围外的文件。
    /// </summary>
    public static bool HasSymbolicLinkComponent(SftpClient sftp, string remotePath)
    {
        var normalized = PathGuard.Normalize(remotePath);
        if (normalized is "/" or ".")
        {
            return false;
        }

        // 逐级拼回去检查；某一级不存在时 IsSymbolicLink 返回 false，
        // 后续操作会自己报文件不存在，不需要在这里提前判断
        var current = string.Empty;
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            if (IsSymbolicLink(sftp, current))
            {
                return true;
            }
        }

        return false;
    }
}