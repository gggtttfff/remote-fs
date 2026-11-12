using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using RemoteFs.Logging;

namespace RemoteFs.Gui;

/// <summary>
/// 日志在列表和详情里的展示形态。行数据在入列时算好，刷新时不再解析原文。
/// </summary>
internal sealed class LogDisplayRow
{
    private const int FieldLimit = 180;
    private const int SummaryLimit = 240;
    private const int SearchLimit = 800;

    /// <summary>超过这个长度就不再格式化 JSON，避免点一行把界面卡住</summary>
    private const int PrettyPrintLimit = 100_000;

    private const int TruncateDisplay = 20_000;

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private LogDisplayRow(LogEntry entry)
    {
        Entry = entry;
    }

    public LogEntry Entry { get; }

    public string TimeText { get; private init; } = string.Empty;

    public string LevelText { get; private init; } = string.Empty;

    public string ToolText { get; private init; } = string.Empty;

    public string HostText { get; private init; } = string.Empty;

    public string ElapsedText { get; private init; } = string.Empty;

    public string ResultText { get; private init; } = string.Empty;

    /// <summary>从入参里抽出的路径、命令等，不含文件正文</summary>
    public string? Target { get; private init; }

    public string Summary { get; private init; } = string.Empty;

    public string SearchText { get; private init; } = string.Empty;

    public Color BackColor { get; private init; }

    public Color ForeColor { get; private init; }

    public static LogDisplayRow Create(LogEntry entry)
    {
        var hint = DescribeArguments(entry.Arguments);
        var host = string.IsNullOrEmpty(entry.Host) ? hint.Host : entry.Host;
        var summary = BuildSummary(entry, hint.Target);
        var colors = ColorsFor(entry);

        return new LogDisplayRow(entry)
        {
            TimeText = FormatTime(entry.Timestamp),
            LevelText = FormatLevel(entry.Level),
            ToolText = FormatTool(entry),
            HostText = host ?? string.Empty,
            ElapsedText = FormatElapsed(entry.ElapsedMilliseconds),
            ResultText = FormatResult(entry.Success),
            Target = hint.Target,
            Summary = summary,
            SearchText = BuildSearch(entry, summary, hint.Target, host),
            BackColor = colors.Back,
            ForeColor = colors.Fore
        };
    }

    /// <summary>
    /// 详情文本。错误放在出入参前面，长正文截断，完整内容仍在日志文件里。
    /// </summary>
    public string BuildDetail()
    {
        var entry = Entry;
        var builder = new StringBuilder();

        void Line(string label, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            builder.Append(label);
            builder.Append("    ");
            builder.AppendLine(value);
        }

        Line("时间", entry.Timestamp);
        Line("级别", LevelText);
        Line("进程", entry.ProcessId.ToString(CultureInfo.InvariantCulture));
        Line("事件", FormatEvent(entry.Event));
        Line("工具", entry.Tool);
        Line("主机", HostText);
        if (entry.ElapsedMilliseconds is long elapsed)
        {
            Line("耗时", $"{elapsed.ToString(CultureInfo.InvariantCulture)} 毫秒");
        }

        Line("结果", ResultText);
        Line("对象", Target);

        if (entry.Event == "tool_timing")
        {
            builder.AppendLine();
            builder.AppendLine("这条只记录耗时，没有出入参。");
            return builder.ToString();
        }

        if (!string.IsNullOrEmpty(entry.Error))
        {
            builder.AppendLine();
            builder.AppendLine("=== 错误 ===");
            builder.AppendLine(FormatPlain(entry.Error));
        }

        if (!string.IsNullOrEmpty(entry.Message))
        {
            builder.AppendLine();
            builder.AppendLine("=== 内容 ===");
            builder.AppendLine(FormatPlain(entry.Message));
        }

        if (entry.Event != "tool_call")
        {
            return builder.ToString();
        }

        builder.AppendLine();
        builder.AppendLine("=== 入参 ===");
        builder.AppendLine(entry.Arguments is null
            ? "(未记录。可在「日志设置」打开「记录完整出入参」)"
            : FormatPayload(entry.Arguments));

        builder.AppendLine();
        builder.AppendLine("=== 出参 ===");
        builder.AppendLine(entry.Response is null ? "(未记录)" : FormatPayload(entry.Response));
        return builder.ToString();
    }

    private static string BuildSummary(LogEntry entry, string? target)
    {
        if (entry.Event == "tool_timing")
        {
            return "耗时追踪";
        }

        if (entry.Event != "tool_call")
        {
            return FirstLine(entry.Message, SummaryLimit) ?? FirstLine(entry.Error, SummaryLimit) ?? string.Empty;
        }

        var problem = FirstLine(entry.Error, 160);
        if (!string.IsNullOrEmpty(problem))
        {
            return Clip(JoinNonEmpty(target, problem), SummaryLimit);
        }

        if (!string.IsNullOrEmpty(target))
        {
            return Clip(target, SummaryLimit);
        }

        return FirstLine(entry.Message, SummaryLimit) ?? FirstLine(entry.Response, 120) ?? string.Empty;
    }

    private static string BuildSearch(LogEntry entry, string summary, string? target, string? host)
    {
        var builder = new StringBuilder();
        Append(entry.Tool);
        Append(host);
        Append(entry.Host);
        Append(target);
        Append(summary);
        Append(entry.Message);
        Append(FirstLine(entry.Error, 300));
        var text = builder.ToString();
        return text.Length <= SearchLimit ? text : text[..SearchLimit];

        void Append(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(value);
        }
    }

    /// <summary>
    /// 只读取路径、命令这类短字段。文件正文往往是超长字符串，读到就跳过，不放进列表。
    /// </summary>
    private static ArgumentHint DescribeArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 8 * 1024 * 1024)
        {
            return default;
        }

        var start = json.AsSpan().TrimStart();
        if (start.Length == 0 || start[0] != '{')
        {
            return default;
        }

        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return default;
            }

            string? host = null;
            string? path = null;
            string? command = null;
            string? pattern = null;
            string? filePattern = null;
            string? source = null;
            string? destination = null;
            string? localPath = null;
            string? remotePath = null;
            string? linkTarget = null;
            string? linkPath = null;
            string? jobId = null;
            string? startLine = null;
            string? endLine = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                var name = reader.GetString();
                if (!reader.Read())
                {
                    break;
                }

                var value = ReadShortValue(ref reader);
                switch (name)
                {
                    case "host":
                        host = value;
                        break;
                    case "path":
                        path = value;
                        break;
                    case "command":
                        command = value;
                        break;
                    case "pattern":
                        pattern = value;
                        break;
                    case "filePattern":
                        filePattern = value;
                        break;
                    case "source":
                        source = value;
                        break;
                    case "destination":
                        destination = value;
                        break;
                    case "localPath":
                        localPath = value;
                        break;
                    case "remotePath":
                        remotePath = value;
                        break;
                    case "target":
                        linkTarget = value;
                        break;
                    case "linkPath":
                        linkPath = value;
                        break;
                    case "jobId":
                        jobId = value;
                        break;
                    case "startLine":
                        startLine = value;
                        break;
                    case "endLine":
                        endLine = value;
                        break;
                }
            }

            return new ArgumentHint(host, ComposeTarget(
                path, command, pattern, filePattern, source, destination,
                localPath, remotePath, linkTarget, linkPath, jobId, startLine, endLine));
        }
        catch (Exception)
        {
            // 坏行不能挡住后面的日志，列表里就当没有可展示的对象
            return default;
        }
    }

    private static string? ComposeTarget(
        string? path,
        string? command,
        string? pattern,
        string? filePattern,
        string? source,
        string? destination,
        string? localPath,
        string? remotePath,
        string? linkTarget,
        string? linkPath,
        string? jobId,
        string? startLine,
        string? endLine)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrEmpty(path))
        {
            parts.Add(DecoratePath(path, startLine, endLine));
        }
        else if (!string.IsNullOrEmpty(linkPath) || !string.IsNullOrEmpty(linkTarget))
        {
            parts.Add(JoinArrow(linkPath, linkTarget));
        }
        else if (!string.IsNullOrEmpty(source) || !string.IsNullOrEmpty(destination))
        {
            parts.Add(JoinArrow(source, destination));
        }
        else if (!string.IsNullOrEmpty(localPath) || !string.IsNullOrEmpty(remotePath))
        {
            parts.Add(JoinArrow(localPath, remotePath));
        }

        Add(command);
        Add(filePattern);
        Add(pattern);
        Add(jobId);

        if (parts.Count == 0)
        {
            return null;
        }

        return Clip(string.Join("  ", parts), SummaryLimit);

        void Add(string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                parts.Add(value);
            }
        }
    }

    private static string DecoratePath(string path, string? startLine, string? endLine)
    {
        if (string.IsNullOrEmpty(startLine))
        {
            return path;
        }

        if (string.IsNullOrEmpty(endLine) || endLine == startLine)
        {
            return $"{path}  第{startLine}行";
        }

        return $"{path}  {startLine}-{endLine}行";
    }

    private static string JoinArrow(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left))
        {
            return right ?? string.Empty;
        }

        if (string.IsNullOrEmpty(right))
        {
            return left;
        }

        return $"{left} → {right}";
    }

    /// <summary>
    /// 短字符串才取出来。超长字符串是文件正文，只跳过，不分配。
    /// </summary>
    private static string? ReadShortValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                if (reader.HasValueSequence || reader.ValueSpan.Length > 2_000)
                {
                    return null;
                }

                var text = reader.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                return Clip(text, FieldLimit);
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var number)
                    ? number.ToString(CultureInfo.InvariantCulture)
                    : null;
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                reader.Skip();
                return null;
            default:
                return null;
        }
    }

    private static (Color Back, Color Fore) ColorsFor(LogEntry entry)
    {
        if (entry.Success == false || entry.Level == nameof(LogLevel.Error))
        {
            return (Color.FromArgb(255, 236, 236), Color.FromArgb(153, 27, 27));
        }

        if (entry.Level == nameof(LogLevel.Warn))
        {
            return (Color.FromArgb(255, 246, 214), Color.FromArgb(138, 90, 0));
        }

        if (entry.Event == "tool_timing")
        {
            return (Color.FromArgb(240, 246, 252), Color.FromArgb(55, 90, 130));
        }

        if (entry.Level == nameof(LogLevel.Debug))
        {
            return (SystemColors.Window, Color.FromArgb(110, 110, 110));
        }

        return (SystemColors.Window, SystemColors.WindowText);
    }

    private static string FormatTime(string timestamp)
    {
        // yyyy-MM-dd HH:mm:ss.fff → MM-dd HH:mm:ss.fff，年份留在详情里
        if (timestamp.Length >= 19 && timestamp[4] == '-' && timestamp[10] == ' ')
        {
            return timestamp[5..];
        }

        return timestamp;
    }

    private static string FormatLevel(string level) => level switch
    {
        nameof(LogLevel.Debug) => "调试",
        nameof(LogLevel.Info) => "信息",
        nameof(LogLevel.Warn) => "警告",
        nameof(LogLevel.Error) => "错误",
        _ => level
    };

    private static string FormatEvent(string eventName) => eventName switch
    {
        "tool_call" => "工具调用",
        "tool_timing" => "耗时追踪",
        "event" => "事件",
        "error" => "错误",
        _ => eventName
    };

    private static string FormatTool(LogEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.Tool))
        {
            return entry.Tool;
        }

        return FormatEvent(entry.Event);
    }

    private static string FormatElapsed(long? milliseconds)
    {
        if (milliseconds is not long ms)
        {
            return string.Empty;
        }

        if (ms < 0)
        {
            ms = 0;
        }

        if (ms < 1_000)
        {
            return $"{ms.ToString(CultureInfo.InvariantCulture)}ms";
        }

        if (ms < 10_000)
        {
            return $"{(ms / 1000d).ToString("0.0", CultureInfo.InvariantCulture)}s";
        }

        if (ms < 60_000)
        {
            return $"{(ms / 1000).ToString(CultureInfo.InvariantCulture)}s";
        }

        var minutes = ms / 60_000;
        var seconds = (ms % 60_000) / 1000;
        return $"{minutes.ToString(CultureInfo.InvariantCulture)}m{seconds.ToString("00", CultureInfo.InvariantCulture)}s";
    }

    private static string FormatResult(bool? success) => success switch
    {
        true => "成功",
        false => "失败",
        _ => string.Empty
    };

    private static string FormatPayload(string text)
    {
        if (text.Length > PrettyPrintLimit)
        {
            return FormatPlain(text);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(document.RootElement, PrettyJson);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static string FormatPlain(string text)
    {
        if (text.Length <= PrettyPrintLimit)
        {
            return text;
        }

        return text[..TruncateDisplay]
            + $"{Environment.NewLine}… 已截断，原文 {text.Length.ToString(CultureInfo.InvariantCulture)} 字符。完整内容在日志文件中。";
    }

    private static string? FirstLine(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var line = text.Split('\n', 2)[0].Trim('\r', ' ', '\t');
        if (line.Length == 0)
        {
            return null;
        }

        return Clip(line, max);
    }

    private static string JoinNonEmpty(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left))
        {
            return right ?? string.Empty;
        }

        if (string.IsNullOrEmpty(right))
        {
            return left;
        }

        return left + "  " + right;
    }

    private static string Clip(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        return text[..max];
    }

    private readonly struct ArgumentHint
    {
        public ArgumentHint(string? host, string? target)
        {
            Host = host;
            Target = target;
        }

        public string? Host { get; }

        public string? Target { get; }
    }
}
