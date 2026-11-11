using System.Text;
using System.Text.Json;
using RemoteFs.Common;
using RemoteFs.Config;
using RemoteFs.Mcp;
using RemoteFs.Services;
using RemoteFs.Ssh;

namespace RemoteFs.Tools;

/// <summary>
/// 远程命令执行工具
/// </summary>
public sealed class CommandTools
{
    private readonly RemoteFsService _service;

    public CommandTools(RemoteFsService service)
    {
        _service = service;
    }

    /// <summary>
    /// 构建命令工具
    /// </summary>
    public List<ToolDefinition> Build()
    {
        return new List<ToolDefinition>
        {
            BuildExecuteCommand(),
            BuildTestConnection(),
            BuildListHosts()
        };
    }

    /// <summary>
    /// 执行远程命令
    /// </summary>
    private ToolDefinition BuildExecuteCommand()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机")),
                ("command", SchemaBuilder.String("要在远程主机执行的 shell 命令")),
                ("timeout", SchemaBuilder.Integer(
                    "前台等待的超时毫秒数，超时后终止整个进程组并返回已产生的输出；默认取主机配置值",
                    1000, 3_600_000)),
                ("background", SchemaBuilder.Boolean(
                    "是否后台运行。开启后立即返回任务编号，用 remote_job_status 增量查看输出", false)),
                ("ttlMinutes", SchemaBuilder.Integer(
                    "后台任务的最长运行分钟数，到期由远程看护进程强制终止；默认取全局配置", 1, 1_440))
            },
            new[] { "command" });

        return new ToolDefinition
        {
            Name = "remote_exec",
            Description = "在远程主机上执行 shell 命令。默认前台等待结束后一次性返回输出；"
                + "长任务（编译、构建）建议设置 background=true 立即拿到任务编号，"
                + "再用 remote_job_status 边跑边读输出、remote_job_kill 终止。"
                + "超时或终止时会下发信号到整个进程组，不会留下孤儿进程。"
                + "注意：命令中的路径防护是尽力而为，变量拼接等写法无法拦截，"
                + "需要严格隔离时应把全局配置 remoteExecPolicy 设为 Deny。",
            InputSchema = schema,
            Annotations = ToolAnnotations.Destructive,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var command = context.RequireString("command");
                var timeout = context.GetInt("timeout");
                var background = context.GetBool("background");
                var ttlMinutes = context.GetInt("ttlMinutes");

                // 命令中引用的绝对路径同样受白名单约束，避免绕过文件工具的限制
                _service.EnsureCommandAllowed(profile, command);

                // 1.1 后台任务必须有兜底时限，否则提交完忘了就会漏跑
                var ttlSeconds = Math.Clamp(
                    (ttlMinutes ?? _service.Config.BackgroundJobTtlMinutes) * 60, 60, 86_400);
                var handle = _service.Jobs.StartJob(profile, command, ttlSeconds);

                // 2.1 后台模式立即返回，让调用方自己轮询
                if (background)
                {
                    return ToolResult.Ok(FormatSubmitted(profile, handle, ttlSeconds));
                }

                // 3.1 前台模式等它结束，超时就终止进程组并保留已产生的输出
                var foregroundTimeout = timeout ?? profile.CommandTimeoutMilliseconds;
                var result = _service.Jobs.WaitForCompletion(profile, handle, foregroundTimeout);

                var text = _service.TruncateOutput(FormatCompleted(profile, command, result));
                var succeeded = !result.TimedOut && result.ExitCode == 0;
                return succeeded ? ToolResult.Ok(text) : ToolResult.Error(text);
            }
        };
    }

    /// <summary>
    /// 后台任务提交后的回执
    /// </summary>
    private static string FormatSubmitted(HostProfile profile, RemoteJobHandle handle, int ttlSeconds)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"任务已提交: {handle.JobId}");
        builder.AppendLine($"主机: {profile.Name}");
        builder.AppendLine($"命令: {handle.Command}");
        builder.AppendLine($"最长运行: {ttlSeconds / 60} 分钟，到期由远程看护进程强制终止");
        builder.AppendLine($"任务目录: {handle.RemoteDirectory}");
        builder.AppendLine("后续: 用 remote_job_status 拉取增量输出，用 remote_job_kill 提前终止。");
        return builder.ToString();
    }

    /// <summary>
    /// 前台任务结束或超时后的结果
    /// </summary>
    private static string FormatCompleted(
        HostProfile profile, string command, ForegroundJobResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"命令: {command}");
        builder.AppendLine($"主机: {profile.Name}");
        builder.AppendLine($"任务: {result.Handle.JobId}");

        if (result.TimedOut)
        {
            builder.AppendLine("状态: 已超时终止，整个进程组已收到终止信号");
        }
        else if (result.ExitCode is null)
        {
            builder.AppendLine("退出码: 未知（进程结束时没有写出退出码）");
        }
        else
        {
            builder.AppendLine($"退出码: {result.ExitCode}");
        }

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            builder.AppendLine(result.TimedOut ? "--- 标准输出（终止前已产生） ---" : "--- 标准输出 ---");
            builder.AppendLine(result.StandardOutput.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            builder.AppendLine(result.TimedOut ? "--- 标准错误（终止前已产生） ---" : "--- 标准错误 ---");
            builder.AppendLine(result.StandardError.TrimEnd());
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput)
            && string.IsNullOrWhiteSpace(result.StandardError))
        {
            builder.AppendLine("（无输出）");
        }

        if (result.Truncated)
        {
            builder.AppendLine(
                "[输出超过单次读取上限，只返回了前面的部分。完整内容可在任务目录的 out / err 文件中查看]");
        }

        return builder.ToString();
    }

    /// <summary>
    /// 测试主机连接
    /// </summary>
    private ToolDefinition BuildTestConnection()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("host", SchemaBuilder.String("目标主机别名，未指定则使用默认主机"))
            });

        return new ToolDefinition
        {
            Name = "remote_test_connection",
            Description = "测试与远程主机的 SSH 连接是否正常，返回延迟、主机密钥指纹和系统信息。"
                + "该工具不做指纹比对，可用于在固化指纹之前确认服务器实际指纹。",
            InputSchema = schema,
            Annotations = ToolAnnotations.ReadOnly,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));

                // 用放宽指纹校验的副本连接，否则「还没固化指纹」会让测试本身失败
                var probeProfile = HostKeyValidator.CreateProbeProfile(profile);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                using var client = _service.OpenSsh(probeProfile, out var fingerprint);
                stopwatch.Stop();

                var builder = new StringBuilder();
                builder.AppendLine($"连接成功: {profile.Username}@{profile.Host}:{profile.Port}");
                builder.AppendLine($"握手耗时: {stopwatch.ElapsedMilliseconds} 毫秒");
                builder.AppendLine($"服务器版本: {client.ConnectionInfo.ServerVersion}");
                builder.AppendLine($"主机密钥指纹: {fingerprint}");

                // 1.1 与已固化的指纹比对，方便发现服务器重装或中间人
                if (string.IsNullOrWhiteSpace(profile.KnownHostKeyFingerprint))
                {
                    builder.AppendLine("指纹固定: 未配置 knownHostKeyFingerprint");
                }
                else if (string.Equals(
                             SshConnectionFactory.NormalizeFingerprint(profile.KnownHostKeyFingerprint),
                             SshConnectionFactory.NormalizeFingerprint(fingerprint),
                             StringComparison.Ordinal))
                {
                    builder.AppendLine("指纹固定: 与配置一致");
                }
                else
                {
                    builder.AppendLine(
                        $"指纹固定: 与配置不一致！配置为 {profile.KnownHostKeyFingerprint}，本次实际为 {fingerprint}");
                }

                return ToolResult.Ok(builder.ToString());
            }
        };
    }

    /// <summary>
    /// 列出已配置主机
    /// </summary>
    private ToolDefinition BuildListHosts()
    {
        var schema = SchemaBuilder.Object(
            new (string, JsonElement)[]
            {
                ("includeOffline", SchemaBuilder.Boolean("是否逐个测试连接以确认可用状态", false))
            });

        return new ToolDefinition
        {
            Name = "remote_list_hosts",
            Description = "列出已配置的远程主机别名与连接信息。需要时可用于确认哪些主机可用。",
            InputSchema = schema,
            Annotations = ToolAnnotations.ReadOnly,
            Handler = context =>
            {
                var includeOffline = context.GetBool("includeOffline");
                var hosts = _service.ConfigStore.LoadHosts();

                if (hosts.Count == 0)
                {
                    // 路径必须如实反映实际位置，写死 ~ 会把用户引到错误的地方
                    var hostsFile = System.IO.Path.Combine(ConfigStore.ConfigDirectory, "hosts.json");
                    return ToolResult.Ok(
                        $"尚未配置任何主机。请使用 remote-fs hosts add <别名> 命令添加，"
                        + $"当前主机配置文件路径为 {hostsFile}。");
                }

                var builder = new StringBuilder();
                builder.AppendLine($"已配置 {hosts.Count} 台主机：");
                foreach (var host in hosts)
                {
                    var scope = host.AllowedRoots.Count > 0
                        ? string.Join(", ", host.AllowedRoots)
                        : "未限制";
                    builder.AppendLine($"- {host.Name}: {host.Username}@{host.Host}:{host.Port} "
                        + $"认证={host.Auth} 允许范围={scope}");

                    if (includeOffline)
                    {
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            // 同样放宽指纹校验，只判断网络与认证是否可用
                            using var client = _service.OpenSsh(
                                HostKeyValidator.CreateProbeProfile(host), out _);
                            stopwatch.Stop();
                            builder.AppendLine($"  状态: 可连接（{stopwatch.ElapsedMilliseconds} 毫秒）");
                        }
                        catch (Exception exception)
                        {
                            builder.AppendLine($"  状态: 不可连接（{exception.Message}）");
                        }
                    }
                }

                return ToolResult.Ok(builder.ToString());
            }
        };
    }
}