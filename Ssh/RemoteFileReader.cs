using System.Text;
using Renci.SshNet;

namespace RemoteFs.Ssh;

/// <summary>
/// 远程文件读取器：按字节区间读取，支持大文件分页，避免一次性拉取撑爆上下文
/// </summary>
public static class RemoteFileReader
{
    /// <summary>
    /// 读取文件内容
    /// </summary>
    /// <param name="sftp">SFTP 客户端</param>
    /// <param name="remotePath">远程文件路径</param>
    /// <param name="offset">起始字节偏移</param>
    /// <param name="length">读取字节数，-1 表示读取到末尾</param>
    /// <param name="maxBytes">单次读取上限</param>
    public static (byte[] Content, long TotalLength, bool Truncated) Read(
        SftpClient sftp, string remotePath, long offset, long length, int maxBytes)
    {
        var totalLength = sftp.GetAttributes(remotePath).Size;

        if (offset < 0)
        {
            offset = 0;
        }
        if (offset >= totalLength)
        {
            return (Array.Empty<byte>(), totalLength, false);
        }

        // 实际读取长度受请求长度与单次上限双重约束
        var effectiveLength = length < 0 ? totalLength - offset : Math.Min(length, totalLength - offset);
        effectiveLength = Math.Min(effectiveLength, maxBytes);
        if (effectiveLength <= 0)
        {
            return (Array.Empty<byte>(), totalLength, false);
        }

        using var stream = sftp.OpenRead(remotePath);
        if (offset > 0)
        {
            stream.Seek(offset, SeekOrigin.Begin);
        }

        var buffer = new byte[effectiveLength];
        var totalRead = 0;
        while (totalRead < effectiveLength)
        {
            var read = stream.Read(buffer, totalRead, (int)(effectiveLength - totalRead));
            if (read <= 0)
            {
                break;
            }
            totalRead += read;
        }

        var content = new byte[totalRead];
        Array.Copy(buffer, content, totalRead);

        // 实际读到的字节数不足文件总长，说明内容被截断
        var truncated = offset + totalRead < totalLength;
        return (content, totalLength, truncated);
    }

    /// <summary>
    /// 解码字节为文本。二进制内容（含 NUL 或非法 UTF-8）返回 null，提示改用下载。
    /// </summary>
    public static string? TryDecodeText(byte[] content)
    {
        if (content.Length == 0)
        {
            return string.Empty;
        }

        foreach (var value in content)
        {
            if (value == 0)
            {
                return null;
            }
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}