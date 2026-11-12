using RemoteFs.Config;

namespace RemoteFs.Logging;

/// <summary>
/// 日志设置，对应全局配置里的 log* 字段
/// </summary>
public sealed class LogSettings
{
    /// <summary>低于该等级的日志不写入</summary>
    public LogLevel Level { get; set; } = LogLevel.Info;

    /// <summary>是否记录工具调用的完整出入参。开启后服务器上的文件内容会明文落盘</summary>
    public bool RecordPayloads { get; set; } = true;

    /// <summary>是否把每次工具调用的耗时单独记一条追踪日志，不含出入参</summary>
    public bool TraceToolTiming { get; set; }

    /// <summary>单个日志文件的大小上限，超过后自动分割</summary>
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>日志保留天数，超过后自动清理</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>最多保留的日志文件数，超出后删除最旧的</summary>
    public int MaxFiles { get; set; } = 20;

    /// <summary>默认设置</summary>
    public static LogSettings Default => new();

    /// <summary>
    /// 从全局配置构造，未配置时用默认值兜底
    /// </summary>
    public static LogSettings FromConfig(AppConfig config)
    {
        return new LogSettings
        {
            Level = config.LogLevel,
            RecordPayloads = config.LogRecordPayloads,
            TraceToolTiming = config.LogTraceToolTiming,
            MaxFileSizeBytes = config.LogMaxFileSizeBytes > 0 ? config.LogMaxFileSizeBytes : 10 * 1024 * 1024,
            RetentionDays = config.LogRetentionDays > 0 ? config.LogRetentionDays : 7,
            MaxFiles = config.LogMaxFiles > 0 ? config.LogMaxFiles : 20
        };
    }
}
