using System.Text;
using Renci.SshNet;
using RemoteFs.Common;

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
    /// <param name="sftp">SFTP 客户端</param>
    /// <param name="remotePath">远程文件路径</param>
    /// <param name="maxBytes">字节预算上限</param>
    /// <param name="maxLines">行数上限</param>
    /// <param name="encodingName">编码名；null 或 auto 表示有 BOM 按 BOM、没有按 UTF-8</param>
    public static IEnumerable<(int Number, string Content)> ReadLines(
        SftpClient sftp, string remotePath, long maxBytes, int maxLines, string? encodingName = null)
    {
        // 1. 解析回退编码：auto 交给 BOM 判定，其余按指定编码宽松解码
        var fallbackEncoding = ResolveFallbackEncoding(encodingName, remotePath);

        using var stream = sftp.OpenRead(remotePath);
        // 2. 宽松解码 + BOM 自动识别：非法字节用替换字符兜底，避免整份文件被跳过
        using var reader = new StreamReader(
            stream, fallbackEncoding, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024);

        var lineNumber = 0;
        var consumedBytes = 0L;
        string? line;
        while (lineNumber < maxLines
               && consumedBytes < maxBytes
               && (line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            // 3. 字节预算按实际生效的编码计算：GBK 下用 UTF-8 估算会明显偏大
            consumedBytes += reader.CurrentEncoding.GetByteCount(line) + 1;
            yield return (lineNumber, line);
        }
    }

    /// <summary>
    /// 解码字节为文本。BOM 识别、二进制判定、多编码解码统一交给编码解析器，
    /// 避免读写两层各写一套解码规则导致行为不一致。
    /// </summary>
    /// <param name="content">待解码内容</param>
    /// <param name="encodingName">编码名；null 或 auto 表示有 BOM 按 BOM、没有按 UTF-8</param>
    /// <param name="truncated">内容是否按字节截断。截断时改用宽松解码，避免切在多字节字符中间被误判</param>
    public static DecodeOutcome DecodeText(
        byte[] content, string? encodingName = null, bool truncated = false)
    {
        return TextEncodingResolver.Decode(content, encodingName, truncated);
    }

    /// <summary>
    /// 解析流式读取使用的回退编码。
    /// 指定编码不可用时记录告警并回退到宽松 UTF-8，不让整个文件读取失败。
    /// </summary>
    private static Encoding ResolveFallbackEncoding(string? encodingName, string remotePath)
    {
        var lenientEncoding = TextEncodingResolver.ResolveLenient(encodingName);
        if (lenientEncoding is not null)
        {
            return lenientEncoding;
        }

        // auto 表示"看 BOM 再定"，不是异常情况，不记告警
        if (TextEncodingResolver.Normalize(encodingName) != TextEncodingResolver.Auto)
        {
            AuditLogger.Warn(
                $"逐行读取时编码 '{encodingName}' 不可用，已回退到 UTF-8: {remotePath}");
        }
        return TextEncodingResolver.LenientUtf8;
    }
}
