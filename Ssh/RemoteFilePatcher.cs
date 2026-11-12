using RemoteFs.Common;

namespace RemoteFs.Ssh;

/// <summary>
/// 行区间替换器。
/// 采用服务端拼接：把替换内容单独传成一个临时文件，再由服务器上的 head / cat / tail 拼回去。
/// 文件本体不经过网络，只在服务器本地读一遍、写一遍，比客户端逐块搬运快两个数量级。
/// head / tail 按字节工作，任何编码的文件都不会被破坏。
/// </summary>
public static class RemoteFilePatcher
{
    /// <summary>行号超出文件范围时的退出码</summary>
    public const int ExitLineOutOfRange = 3;

    /// <summary>
    /// 生成服务端拼接命令。
    /// 成功时标准输出末行为 <c>TOTAL:&lt;总行数&gt;</c>，行号越界时退出码为 <see cref="ExitLineOutOfRange"/>
    /// </summary>
    /// <param name="remotePath">目标文件</param>
    /// <param name="payloadPath">存放替换内容的临时文件</param>
    /// <param name="temporaryPath">拼接结果临时文件，与目标同目录</param>
    /// <param name="startLine">起始行号（含）</param>
    /// <param name="endLine">结束行号（含）</param>
    public static string BuildSpliceCommand(
        string remotePath, string payloadPath, string temporaryPath, int startLine, int endLine)
    {
        var file = ShellQuoting.Quote(remotePath);
        var payload = ShellQuoting.Quote(payloadPath);
        var temporary = ShellQuoting.Quote(temporaryPath);
        var previousLines = Math.Max(0, startLine - 1);
        var nextLine = endLine + 1;

        var lines = new[]
        {
            "set -e",
            // 文件名一律走重定向传入：awk / head / tail 对 -- 分隔符的支持不一致，
            // 而路径又以 / 开头（绝不是 - 开头），重定向最稳
            // awk 的 NR 能正确处理末行没有换行符的情况，wc -l 会少数一行
            $"TOTAL=$(awk 'END{{print NR}}' < {file})",
            $"if [ \"$TOTAL\" -lt {startLine} ]; then echo \"OUT_OF_RANGE:$TOTAL\"; exit {ExitLineOutOfRange}; fi",
            $"head -n {previousLines} < {file} > {temporary}",
            $"cat < {payload} >> {temporary}",
            $"tail -n +{nextLine} < {file} >> {temporary}",
            $"mv -f -- {temporary} {file}",
            "echo \"TOTAL:$TOTAL\""
        };
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 从命令输出里取总行数；取不到返回 null
    /// </summary>
    public static long? ParseTotalLines(string standardOutput)
    {
        foreach (var line in standardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("TOTAL:", StringComparison.Ordinal)
                && long.TryParse(trimmed["TOTAL:".Length..], out var total))
            {
                return total;
            }
        }
        return null;
    }
}