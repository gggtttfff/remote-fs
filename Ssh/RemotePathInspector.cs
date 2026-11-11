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
}