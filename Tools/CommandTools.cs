using System.Text;
using System.Text.Json;
using RemoteFs.Common;
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
                ("timeout", SchemaBuilder.Integer("超时毫秒数，默认取主机配置值", 1000, 600000))
            },
            new[] { "command" });

        return new ToolDefinition
        {
            Name = "remote_exec",
            Description = "在远程主机上执行 shell 命令并返回标准输出、标准错误和退出码。"
                + "适用于查看日志、重启服务、检查系统状态等非纯文件操作场景。"
                + "注意：命令中的路径防护是尽力而为，变量拼接等写法无法拦截，"
                + "需要严格隔离时应把全局配置 remoteExecPolicy 设为 Deny。",
            InputSchema = schema,
            Annotations = ToolAnnotations.Destructive,
            Handler = context =>
            {
                var profile = _service.ResolveHost(context.GetString("host"));
                var command = context.RequireString("command");
                var timeout = context.GetInt("timeout");

                // 命令中引用的绝对路径同样受白名单约束，避免绕过文件工具的限制
                _service.EnsureCommandAllowed(profile, command);

                var output = _service.ExecuteCommand(profile, command, timeout);

                var builder = new StringBuilder();
                builder.AppendLine($"命令: {command}");
                builder.AppendLine($"主机: {profile.Name}");
                builder.AppendLine($"退出码: {output.ExitCode}");

                if (!string.IsNullOrWhiteSpace(output.StandardOutput))
                {
                    builder.AppendLine("--- 标准输出 ---");
                    builder.AppendLine(output.StandardOutput.TrimEnd());
                }
                if (!string.IsNullOrWhiteSpace(output.StandardError))
                {
                    builder.AppendLine("--- 标准错误 ---");
                    builder.AppendLine(output.StandardError.TrimEnd());
                }
                if (string.IsNullOrWhiteSpace(output.StandardOutput)
                    && string.IsNullOrWhiteSpace(output.StandardError))
                {
                    builder.AppendLine("（无输出）");
                }

                var text = _service.TruncateOutput(builder.ToString());
                return output.ExitCode == 0
                    ? ToolResult.Ok(text)
                    : ToolResult.Error(text);
            }
        };
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
                    return ToolResult.Ok(
                        "尚未配置任何主机。请使用 remote-fs hosts add <别名> 命令添加，"
                        + "配置文件位于 ~/.remote-fs/hosts.json。");
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