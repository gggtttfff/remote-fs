using System.Text;
using Renci.SshNet;

namespace RemoteFs.Ssh;

/// <summary>
/// 远程文件写入器
/// </summary>
public static class RemoteFileWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// 写入文本内容，可选择追加模式
    /// </summary>
    public static void WriteText(SftpClient sftp, string remotePath, string content, bool append)
    {
        if (append)
        {
            using var writer = sftp.AppendText(remotePath, Utf8NoBom);
            writer.Write(content);
            return;
        }
        sftp.WriteAllText(remotePath, content, Utf8NoBom);
    }

    /// <summary>
    /// 原子写入：先写临时文件再重命名，避免写入中断导致文件损坏。
    /// 临时文件与目标同目录，保证重命名是同分区原子操作。
    /// </summary>
    public static void WriteTextAtomic(SftpClient sftp, string remotePath, string content)
    {
        var directory = GetParentDirectory(remotePath);
        var temporaryPath = PathGuard.Combine(directory, $".{Path.GetFileName(remotePath)}.tmp.{Guid.NewGuid():N}");
        try
        {
            sftp.WriteAllText(temporaryPath, content, Utf8NoBom);
            // isPosix=true 时重命名会覆盖已存在的目标文件
            sftp.RenameFile(temporaryPath, remotePath, true);
        }
        catch
        {
            // 失败时清理临时文件，避免残留
            TryDelete(sftp, temporaryPath);
            throw;
        }
    }

    /// <summary>
    /// 递归创建目录
    /// </summary>
    public static void EnsureDirectory(SftpClient sftp, string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath) || remotePath == "/")
        {
            return;
        }
        if (sftp.Exists(remotePath))
        {
            return;
        }

        var parent = GetParentDirectory(remotePath);
        if (!string.IsNullOrEmpty(parent) && parent != "/" && !sftp.Exists(parent))
        {
            EnsureDirectory(sftp, parent);
        }

        try
        {
            sftp.CreateDirectory(remotePath);
        }
        catch (Exception) when (sftp.Exists(remotePath))
        {
            // 并发场景下目录可能已被创建，忽略
        }
    }

    /// <summary>
    /// 获取父目录路径
    /// </summary>
    public static string GetParentDirectory(string path)
    {
        var normalized = path.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            return "/";
        }
        return normalized[..lastSlash];
    }

    /// <summary>
    /// 删除文件，失败时静默忽略
    /// </summary>
    public static void TryDelete(SftpClient sftp, string path)
    {
        try
        {
            if (sftp.Exists(path))
            {
                sftp.DeleteFile(path);
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }
}