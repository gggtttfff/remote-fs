using System.Text;
using RemoteFs.Logging;

namespace RemoteFs.Common;

/// <summary>
/// 审计日志：所有远程操作留痕，便于排查问题。
/// 文件写入 exe 同级的 log 目录（JSONL 格式），同时向 stderr 输出可读文本。
/// stderr 绝不能用来承载 MCP 的协议数据。
/// </summary>
public static class AuditLogger
{
    private static readonly object Lock = new();
    private static readonly Lazy<TextWriter> ErrorWriter = new(() =>
        new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });

    private static LogFileWriter? _fileWriter;
    private static LogSettings _settings = LogSettings.Default;
    private static bool _exitHookRegistered;

    /// <summary>是否写日志，由全局配置 auditLogEnabled 控制</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>当前日志设置</summary>
    public static LogSettings Settings => _settings;

    /// <summary>日志目录</summary>
    public static string LogDirectory => _fileWriter?.Directory ?? LogFileWriter.DefaultDirectory;

    /// <summary>日志是否被禁用（目录不可写等）</summary>
    public static bool IsDisabled => _fileWriter?.IsDisabled ?? true;

    /// <summary>被禁用的原因</summary>
    public static string? DisabledReason => _fileWriter?.DisabledReason;

    /// <summary>
    /// 初始化日志写入器。目录不可写时日志禁用，但不影响其他功能。
    /// </summary>
    public static void Initialize(LogSettings settings, string? directory = null)
    {
        lock (Lock)
        {
            _settings = settings;
            _fileWriter?.Dispose();
            _fileWriter = new LogFileWriter(settings);
            _fileWriter.Initialize(directory);

            // 兜底：异常退出路径也尽量把最后一批日志落盘
            if (!_exitHookRegistered)
            {
                _exitHookRegistered = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
            }

            if (_fileWriter.IsDisabled)
            {
                // 用 stderr 报，不写文件（文件可能根本写不了）
                WriteErrorText("WARN", $"日志功能已禁用: {_fileWriter.DisabledReason}");
            }
        }
    }

    /// <summary>
    /// 热更新日志设置，不重建写入器
    /// </summary>
    public static void UpdateSettings(LogSettings settings)
    {
        lock (Lock)
        {
            _settings = settings;
        }
    }

    /// <summary>清理过期日志，返回删除的文件数</summary>
    public static int Cleanup()
    {
        lock (Lock)
        {
            return _fileWriter?.Cleanup() ?? 0;
        }
    }

    /// <summary>
    /// 立即把攒着的日志落盘。进程退出前必须调用。
    /// </summary>
    public static void Flush()
    {
        lock (Lock)
        {
            _fileWriter?.Flush();
        }
    }

    /// <summary>清空全部日志</summary>
    public static bool ClearAll()
    {
        lock (Lock)
        {
            return _fileWriter?.ClearAll() ?? false;
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    /// <summary>
    /// 记录一次工具调用的完整信息：入参、出参、耗时、异常。
    /// </summary>
    /// <param name="tool">工具名</param>
    /// <param name="host">目标主机别名</param>
    /// <param name="argumentsJson">入参原文</param>
    /// <param name="response">出参原文</param>
    /// <param name="elapsedMilliseconds">耗时毫秒</param>
    /// <param name="success">是否成功</param>
    /// <param name="error">异常信息与堆栈</param>
    public static void ToolCall(
        string tool,
        string? host,
        string argumentsJson,
        string? response,
        long elapsedMilliseconds,
        bool success,
        string? error)
    {
        if (!Enabled)
        {
            return;
        }

        var level = success ? LogLevel.Info : LogLevel.Error;
        if (level < _settings.Level)
        {
            return;
        }

        var entry = new LogEntry
        {
            Timestamp = Now(),
            Level = level.ToString(),
            ProcessId = Environment.ProcessId,
            Event = "tool_call",
            Tool = tool,
            Host = host,
            ElapsedMilliseconds = elapsedMilliseconds,
            Success = success,
            Arguments = _settings.RecordPayloads ? argumentsJson : null,
            Response = _settings.RecordPayloads ? response : null,
            Error = error
        };

        WriteEntry(entry, $"工具 {tool} {(success ? "完成" : "失败")} 耗时 {elapsedMilliseconds}ms");
    }

    private static void Write(LogLevel level, string message)
    {
        if (!Enabled || level < _settings.Level)
        {
            return;
        }

        var entry = new LogEntry
        {
            Timestamp = Now(),
            Level = level.ToString(),
            ProcessId = Environment.ProcessId,
            Event = "event",
            Message = message
        };

        WriteEntry(entry, message);
    }

    private static void WriteEntry(LogEntry entry, string stderrText)
    {
        lock (Lock)
        {
            try
            {
                WriteErrorText(entry.Level, stderrText);
                _fileWriter?.Write(entry);
            }
            catch
            {
                // 日志写入失败不能影响主流程
            }
        }
    }

    private static void WriteErrorText(string level, string message)
    {
        try
        {
            ErrorWriter.Value.WriteLine($"{Now()} [{level}] {message}");
        }
        catch
        {
            // stderr 不可写时忽略
        }
    }

    private static string Now() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
}
