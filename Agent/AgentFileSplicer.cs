namespace RemoteFs.Agent;

/// <summary>
/// 在本机按行拼接文件。语义对齐 head / tail：以 \n 分行，末行没有换行符也算一行。
/// 文件本体只在本地流过一遍，不把整份内容收进内存。
/// </summary>
internal static class AgentFileSplicer
{
    public static long CountLines(string path)
    {
        using var input = File.OpenRead(path);
        long lines = 0;
        var any = false;
        var last = 0;
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            any = true;
            last = buffer[read - 1];
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] == (byte)'\n')
                {
                    lines++;
                }
            }
        }

        if (any && last != (byte)'\n')
        {
            lines++;
        }

        return lines;
    }

    /// <summary>
    /// 用 payload 替换 [startLine, endLine]，结果写到 destination。调用方负责确认行号没有越界。
    /// </summary>
    public static void Splice(string source, string destination, int startLine, int endLine, byte[] payload)
    {
        using var input = File.OpenRead(source);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        CopyOrSkip(input, output, startLine - 1);
        CopyOrSkip(input, null, endLine - startLine + 1);
        if (payload.Length > 0)
        {
            output.Write(payload, 0, payload.Length);
        }

        input.CopyTo(output);
    }

    /// <summary>
    /// 从当前位置复制或跳过指定行数。output 为 null 时只跳过。
    /// </summary>
    private static void CopyOrSkip(Stream input, Stream? output, int lineCount)
    {
        if (lineCount <= 0)
        {
            return;
        }

        var buffer = new byte[64 * 1024];
        var found = 0;
        while (found < lineCount)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return;
            }

            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != (byte)'\n')
                {
                    continue;
                }

                found++;
                if (found != lineCount)
                {
                    continue;
                }

                output?.Write(buffer, 0, index + 1);
                var remain = read - (index + 1);
                if (remain > 0 && input.CanSeek)
                {
                    input.Seek(-remain, SeekOrigin.Current);
                }

                return;
            }

            output?.Write(buffer, 0, read);
        }
    }
}
