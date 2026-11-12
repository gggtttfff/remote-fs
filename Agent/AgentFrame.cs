using System.Buffers.Binary;
using System.Text;

namespace RemoteFs.Agent;

/// <summary>
/// 备用通道的帧：4 字节大端长度 + UTF-8 JSON。一条连接只传一帧请求和一帧响应。
/// </summary>
internal static class AgentFrame
{
    /// <summary>单帧上限。工具参数里的文件内容会整段进来，留出比默认读取上限更大的余量。</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, string json, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length is <= 0 or > MaxBytes)
        {
            throw new InvalidOperationException($"帧长度 {payload.Length} 超出 1 到 {MaxBytes} 字节");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// 读一帧。对端正常关闭且一个字节都没读到时返回 null。
    /// </summary>
    public static async Task<string?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, allowEmpty: true, cancellationToken))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaxBytes)
        {
            throw new InvalidOperationException($"帧长度 {length} 非法");
        }

        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, allowEmpty: false, cancellationToken))
        {
            throw new InvalidOperationException("连接在帧中间断开");
        }

        return Encoding.UTF8.GetString(payload);
    }

    private static async Task<bool> ReadExactAsync(
        Stream stream, byte[] buffer, bool allowEmpty, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                if (offset == 0 && allowEmpty)
                {
                    return false;
                }

                throw new InvalidOperationException("连接在帧中间断开");
            }

            offset += read;
        }

        return true;
    }
}
