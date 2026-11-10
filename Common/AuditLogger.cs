using System.Text;

namespace RemoteFs.Common;

/// <summary>
/// 审计日志：所有远程操作留痕，便于排查问题。
/// 写入 stderr，避免污染 MCP 的 stdout 协议通道。
/// </summary>
public static class AuditLogger
{
    private static readonly object Lock = new();
    private static readonly Lazy<TextWriter> ErrorWriter = new(() =>
        new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });

    /// <summary>日志文件路径，为空表示只输出到 stderr</summary>
    public static string? LogFilePath { get; set; }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (Lock)
        {
            try
            {
                ErrorWriter.Value.WriteLine(line);
                if (!string.IsNullOrEmpty(LogFilePath))
                {
                    File.AppendAllText(LogFilePath, line + Environment.NewLine);
                }
            }
            catch
            {
                // 日志写入失败不能影响主流程
            }
        }
    }
}