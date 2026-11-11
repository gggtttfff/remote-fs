using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteFs.Logging;

/// <summary>
/// 日志条目，序列化成 JSONL 的一行
/// </summary>
public sealed class LogEntry
{
    public string Timestamp { get; init; } = string.Empty;

    public string Level { get; init; } = "Info";

    public int ProcessId { get; init; }

    /// <summary>事件类型：tool_call / event / error</summary>
    public string Event { get; init; } = "event";

    public string? Message { get; init; }

    public string? Tool { get; init; }

    public string? Host { get; init; }

    public long? ElapsedMilliseconds { get; init; }

    public bool? Success { get; init; }

    /// <summary>入参原文（JSON 字符串）</summary>
    public string? Arguments { get; init; }

    /// <summary>出参原文</summary>
    public string? Response { get; init; }

    /// <summary>错误信息与堆栈</summary>
    public string? Error { get; init; }

    /// <summary>序列化成 JSONL 的一行</summary>
    public string ToJsonLine()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    /// <summary>
    /// 从 JSONL 的一行反序列化，解析失败返回 null
    /// </summary>
    public static LogEntry? FromJsonLine(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<LogEntry>(line, JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
