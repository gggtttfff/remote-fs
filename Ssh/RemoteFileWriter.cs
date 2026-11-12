using System.Text;
using Renci.SshNet;
using RemoteFs.Common;

namespace RemoteFs.Ssh;

/// <summary>
/// 远程文件写入器
/// </summary>
public static class RemoteFileWriter
{
    /// <summary>
    /// 写入文本内容，可选择追加模式。
    /// 采用字节级写入：BOM 完全由 writeBom 决定，不会被编码器偷偷带进文件。
    /// </summary>
    /// <param name="sftp">SFTP 客户端</param>
    /// <param name="remotePath">远程文件路径</param>
    /// <param name="content">写入内容</param>
    /// <param name="append">true 为追加写，false 为覆盖写</param>
    /// <param name="encoding">目标编码；null 表示不带 BOM 的 UTF-8</param>
    /// <param name="writeBom">是否写入 BOM。追加写时该参数无效，永远不写 BOM</param>
    public static void WriteText(
        SftpClient sftp,
        string remotePath,
        string content,
        bool append,
        Encoding? encoding = null,
        bool writeBom = false)
    {
        // 1. 未指定编码时用不带 BOM 的 UTF-8，与历史行为保持一致
        var targetEncoding = encoding ?? TextEncodingResolver.Utf8NoBom;

        if (append)
        {
            // 2.1 追加写：绝不写 BOM，否则会在文件中间插入 BOM 字节
            //     Encoding.GetBytes 不含 preamble，绕开 StreamWriter 的自动 BOM 行为
            var appendPayload = targetEncoding.GetBytes(content);
            using var appendWriter = sftp.AppendText(remotePath, TextEncodingResolver.Utf8NoBom);
            appendWriter.BaseStream.Write(appendPayload, 0, appendPayload.Length);
            appendWriter.BaseStream.Flush();
            return;
        }

        // 2.2 覆盖写：先截断再写入，避免旧内容残留
        var overwritePayload = TextEncodingResolver.EncodeBytes(content, targetEncoding, writeBom);
        using var output = sftp.Create(remotePath);
        output.Write(overwritePayload, 0, overwritePayload.Length);
    }

    /// <summary>
    /// 原子写入：先写临时文件再重命名，避免写入中断导致文件损坏。
    /// 临时文件与目标同目录，保证重命名是同分区原子操作。
    /// </summary>
    /// <param name="sftp">SFTP 客户端</param>
    /// <param name="remotePath">远程文件路径</param>
    /// <param name="content">写入内容</param>
    /// <param name="encoding">目标编码；null 表示不带 BOM 的 UTF-8</param>
    /// <param name="writeBom">是否写入 BOM</param>
    public static void WriteTextAtomic(
        SftpClient sftp,
        string remotePath,
        string content,
        Encoding? encoding = null,
        bool writeBom = false)
    {
        // 1. 未指定编码时用不带 BOM 的 UTF-8，与历史行为保持一致
        var targetEncoding = encoding ?? TextEncodingResolver.Utf8NoBom;
        var directory = GetParentDirectory(remotePath);
        var temporaryPath = PathGuard.Combine(directory, $".{Path.GetFileName(remotePath)}.tmp.{Guid.NewGuid():N}");

        try
        {
            // 2. 字节级写入临时文件，BOM 由 writeBom 决定
            var payload = TextEncodingResolver.EncodeBytes(content, targetEncoding, writeBom);
            using (var output = sftp.Create(temporaryPath))
            {
                output.Write(payload, 0, payload.Length);
            }

            // 3. isPosix=true 时重命名会覆盖已存在的目标文件
            sftp.RenameFile(temporaryPath, remotePath, true);
        }
        catch (Exception exception)
        {
            // 4. 失败时清理临时文件，避免残留
            AuditLogger.Error($"原子写入失败，清理临时文件 {temporaryPath}: {exception.Message}");
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
