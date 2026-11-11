using System.Text;
using System.Text.Json;
using RemoteFs.Common;
using RemoteFs.Config;
using RemoteFs.Mcp;
using RemoteFs.Services;

namespace RemoteFs.Tools;

/// <summary>
/// 远程任务工具：查询任务状态、列出任务、终止任务。
/// 任务的提交复用 remote_exec 的 background 参数，不另开一个工具。
/// </summary>
public sealed class JobTools
{
    private const int DefaultMaxBytes = 65_536;
    private const int MaxCommandDisplayLength = 80;

    private readonly RemoteFsService _service;

    public JobTools(RemoteFsService service)
    {
        _service = service;
    }

    /// <summary>
    /// 构建全部任务工具
    /// </summary>
    public List<ToolDefinition> Build()
    {
        return new List<ToolDefinition>
        {
            BuildStatus(),
            BuildList(),
            BuildKill()
        };
    }

    /// <summary>
    /// 增量读取任务输出并返回当前状态
    /// </summary>
    private ToolDefinition BuildStatus()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("jobId", SchemaBuilder.String("任务编号，即 remote_exec 返回的任务号")),
                ("stdoutOffset", SchemaBuilder.Integer(
                    "标准输出的起始字节偏移，用上次返回的 nextStdoutOffset 继续读取", 0, int.MaxValue)),
                ("stderrOffset", SchemaBuilder.Integer(
                    "标准错误的起始字节偏移，用上次返回的 nextStderrOffset 继续读取", 0, int.MaxValue)),
                ("maxBytes", SchemaBuilder.Integer("本次最多返回的字节数，默认 65536", 1024, 1_048_576))
            },
            new[] { "jobId" });

        return new ToolDefinition
        {
            Name = "remote_job_status",
            Description = "查看远程任务的状态并增量拉取新增输出。"
                + "把上次返回的 nextStdoutOffset / nextStderrOffset 传回来即可只读新增部分，不会重复。"
                + "后台编译、构建这类长任务用它可以边跑边看日志。",
            InputSchema = schema,
            Annotations = ToolAnnotations.ReadOnly,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var jobId = context.RequireString("jobId");
                var stdoutOffset = context.GetLong("stdoutOffset") ?? 0;
                var stderrOffset = context.GetLong("stderrOffset") ?? 0;
                var maxBytes = Math.Clamp(
                    context.GetInt("maxBytes") ?? DefaultMaxBytes, 1024, 1_048_576);

                var handle = _service.Jobs.FindJob(profile, jobId);
                if (handle is null)
                {
                    return ToolResult.Error(
                        $"主机 {profile.Name} 上找不到任务 {jobId}。"
                        + "请用 remote_job_list 确认任务编号，或检查任务目录是否已被清理。");
                }

                var (stdout, stderr, info) = _service.Jobs.ReadOutput(
                    profile, handle, stdoutOffset, stderrOffset, maxBytes);

                return ToolResult.Ok(_service.TruncateOutput(FormatStatus(info, stdout, stderr)));
            }
        };
    }

    /// <summary>
    /// 列出远程主机上的任务
    /// </summary>
    private ToolDefinition BuildList()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("limit", SchemaBuilder.Integer("最多返回的任务数，默认 20", 1, 200)),
                ("includeFinished", SchemaBuilder.Boolean("是否包含已结束的任务", true))
            },
            Array.Empty<string>());

        return new ToolDefinition
        {
            Name = "remote_job_list",
            Description = "列出远程主机上的任务，按时间从新到旧。"
                + "服务重启后仍能看到遗留在服务器上的任务，用来发现忘记清理的长任务。",
            InputSchema = schema,
            Annotations = ToolAnnotations.ReadOnly,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var limit = Math.Clamp(context.GetInt("limit") ?? 20, 1, 200);
                var includeFinished = context.GetBool("includeFinished", true);

                var jobs = _service.Jobs.List(profile, limit, includeFinished);
                if (jobs.Count == 0)
                {
                    return ToolResult.Ok($"主机 {profile.Name} 上没有符合条件的任务");
                }

                var builder = new StringBuilder();
                builder.AppendLine($"主机 {profile.Name} 共 {jobs.Count} 个任务：");
                foreach (var job in jobs)
                {
                    builder.AppendLine(FormatListEntry(job));
                }
                return ToolResult.Ok(_service.TruncateOutput(builder.ToString()));
            }
        };
    }

    /// <summary>
    /// 终止任务
    /// </summary>
    private ToolDefinition BuildKill()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("jobId", SchemaBuilder.String("要终止的任务编号")),
                ("force", SchemaBuilder.Boolean("是否直接 KILL，不给进程收尾时间", false))
            },
            new[] { "jobId" });

        return new ToolDefinition
        {
            Name = "remote_job_kill",
            Description = "终止远程任务。默认先 TERM 再 KILL，force=true 直接 KILL。"
                + "信号下发到整个进程组，命令派生的子进程一并结束。"
                + "终止前会核对进程启动时间，避免 pid 被复用后误杀无关进程。",
            InputSchema = schema,
            Annotations = ToolAnnotations.Destructive,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var jobId = context.RequireString("jobId");
                var force = context.GetBool("force");

                var handle = _service.Jobs.FindJob(profile, jobId);
                if (handle is null)
                {
                    return ToolResult.Error(
                        $"主机 {profile.Name} 上找不到任务 {jobId}。可先用 remote_job_list 确认任务编号。");
                }

                var (success, message) = _service.Jobs.Kill(profile, handle, force);
                return success ? ToolResult.Ok(message) : ToolResult.Error(message);
            }
        };
    }

    /// <summary>
    /// 状态查询的输出
    /// </summary>
    private static string FormatStatus(
        RemoteJobInfo info, JobOutputChunk stdout, JobOutputChunk stderr)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"任务: {info.JobId}");
        builder.AppendLine($"状态: {DescribeState(info)}");
        builder.AppendLine($"命令: {info.Command}");
        builder.AppendLine($"任务目录: {info.RemoteDirectory}");
        builder.AppendLine(
            $"输出: 标准输出 {RemoteFsService.FormatSize(info.StdoutBytes)}，"
            + $"标准错误 {RemoteFsService.FormatSize(info.StderrBytes)}");

        AppendChunk(builder, "标准输出", stdout);
        AppendChunk(builder, "标准错误", stderr);

        builder.AppendLine($"nextStdoutOffset: {stdout.NextOffset}");
        builder.AppendLine($"nextStderrOffset: {stderr.NextOffset}");
        builder.AppendLine($"[本次已读到偏移 {stdout.NextOffset} / {stderr.NextOffset}，继续读取时把这两个值传回来]");

        return builder.ToString();
    }

    /// <summary>
    /// 追加一段增量输出
    /// </summary>
    private static void AppendChunk(StringBuilder builder, string title, JobOutputChunk chunk)
    {
        if (string.IsNullOrEmpty(chunk.Text))
        {
            builder.AppendLine($"--- {title}（无新增） ---");
            return;
        }

        builder.AppendLine($"--- {title} ---");
        builder.AppendLine(chunk.Text.TrimEnd());
        if (chunk.HasMore)
        {
            builder.AppendLine($"[还有更多内容未返回，请带 nextOffset 继续读取]");
        }
    }

    /// <summary>
    /// 状态文案，运行中附上已运行时长
    /// </summary>
    private static string DescribeState(RemoteJobInfo info)
    {
        var description = RemoteJobStateText.Describe(info.State);
        if (info.State == RemoteJobState.Running && info.Elapsed is not null)
        {
            return $"{description}（已运行 {FormatDuration(info.Elapsed.Value)}）";
        }
        if (info.State == RemoteJobState.Completed && info.ExitCode is not null)
        {
            return $"{description}（退出码 {info.ExitCode}）";
        }
        return description;
    }

    /// <summary>
    /// 列表中的一行
    /// </summary>
    private static string FormatListEntry(RemoteJobInfo job)
    {
        var command = job.Command.ReplaceLineEndings(" ");
        if (command.Length > MaxCommandDisplayLength)
        {
            command = command[..MaxCommandDisplayLength] + "...";
        }

        var timing = job.State == RemoteJobState.Running && job.Elapsed is not null
            ? $"已运行 {FormatDuration(job.Elapsed.Value)}"
            : job.StartedAtUtc is null
                ? "时间未知"
                : $"{job.StartedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";

        return $"- {job.JobId}  {DescribeState(job)}  {timing}  "
            + $"输出 {RemoteFsService.FormatSize(job.StdoutBytes + job.StderrBytes)}  $ {command}";
    }

    /// <summary>
    /// 时长格式化
    /// </summary>
    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分";
        }
        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.Minutes} 分 {duration.Seconds} 秒";
        }
        return $"{Math.Max(0, duration.Seconds)} 秒";
    }
}