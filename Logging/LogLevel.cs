namespace RemoteFs.Logging;

/// <summary>
/// 日志等级，数值越大越严重
/// </summary>
public enum LogLevel
{
    /// <summary>内部细节：连接池取还、配置重载</summary>
    Debug = 0,

    /// <summary>常规业务事件，含每次工具调用的完整出入参</summary>
    Info = 1,

    /// <summary>兜底降级、跳过文件、指纹未固化</summary>
    Warn = 2,

    /// <summary>异常与失败</summary>
    Error = 3
}
