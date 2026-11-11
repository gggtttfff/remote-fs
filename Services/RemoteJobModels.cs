namespace RemoteFs.Services;

/// <summary>
/// 远程任务状态
/// </summary>
public enum RemoteJobState
{
    /// <summary>进程仍在运行</summary>
    Running,

    /// <summary>命令正常结束，退出码可用</summary>
    Completed,

    /// <summary>超过 TTL 被看护进程终止</summary>
    TimedOut,

    /// <summary>被 remote_job_kill 手动终止</summary>
    Terminated,

    /// <summary>进程消失但没有留下退出码，通常是被 KILL 或 OOM</summary>
    Lost
}

/// <summary>
/// 已提交任务的句柄：定位远程任务目录所需的最小信息
/// </summary>
public sealed record RemoteJobHandle(
    string HostName,
    string JobId,
    string RemoteDirectory,
    string Command,
    DateTime StartedAtUtc);

/// <summary>
/// 任务快照，供状态查询与列表展示
/// </summary>
public sealed class RemoteJobInfo
{
    public required string JobId { get; init; }

    public required string RemoteDirectory { get; init; }

    public string Command { get; init; } = string.Empty;

    public RemoteJobState State { get; init; }

    public int? ExitCode { get; init; }

    public DateTime? StartedAtUtc { get; init; }

    public long StdoutBytes { get; init; }

    public long StderrBytes { get; init; }

    /// <summary>
    /// 已运行时长；任务结束后返回 null
    /// </summary>
    public TimeSpan? Elapsed { get; init; }
}

/// <summary>
/// 增量读取的一段输出
/// </summary>
public sealed record JobOutputChunk(string Text, long NextOffset, bool HasMore);

/// <summary>
/// 任务状态的展示文本
/// </summary>
public static class RemoteJobStateText
{
    public static string Describe(RemoteJobState state)
    {
        return state switch
        {
            RemoteJobState.Running => "运行中",
            RemoteJobState.Completed => "已结束",
            RemoteJobState.TimedOut => "已超时终止",
            RemoteJobState.Terminated => "已手动终止",
            _ => "异常终止"
        };
    }
}