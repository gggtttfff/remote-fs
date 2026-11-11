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
    /// 逐行读取，用于搜索等场景，避免整份文件读进内存。
    /// 返回行号与行内容，达到字节或行数上限即停止。
    /// </summary>
    public static IEnumerable<(int Number, string Content)> ReadLines(
        SftpClient sftp, string remotePath, long maxBytes, int maxLines)
    {
        using var stream = sftp.OpenRead(remotePath);
        // 宽松解码：搜索场景下宁可多几个替换字符，也不要整份文件被跳过
        using var reader = new StreamReader(
            stream, new UTF8Encoding(false, throwOnInvalidBytes: false),
            detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024);

        var lineNumber = 0;
        var consumedBytes = 0L;
        string? line;
        while (lineNumber < maxLines
               && consumedBytes < maxBytes
               && (line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            consumedBytes += Encoding.UTF8.GetByteCount(line) + 1;
            yield return (lineNumber, line);
        }
    }

    /// <summary>
    /// 解码字节为文本。二进制内容（含 NUL 或非法 UTF-8）返回 null，提示改用下载。
    /// </summary>
    /// <param name="content">待解码内容</param>
    /// <param name="truncated">内容是否按字节截断。截断时改用宽松解码，避免切在多字节字符中间被误判为二进制</param>
    public static string? TryDecodeText(byte[] content, bool truncated = false)
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

        if (truncated)
        {
            var lenient = new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(content);
            // 去掉截断点产生的替换字符，避免在输出里留一个乱码方块
            return lenient.TrimEnd('�');
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