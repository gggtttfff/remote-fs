using System.Text.Json.Nodes;
using RemoteFs.Common;
using RemoteFs.Config;

namespace RemoteFs.Agent;

/// <summary>
/// Windows 端在 SSH 连接失败后，把同一次工具调用发到服务器的备用端口。
/// </summary>
public static class AgentChannelClient
{
    /// <summary>
    /// 不该走备用通道时返回 null，调用方继续报告原来的 SSH 错误。
    /// </summary>
    public static ToolResult? TryFallback(string toolName, JsonObject arguments, Exception failure)
    {
        if (failure is not SshUnavailableException unavailable)
        {
            return null;
        }

        var profile = unavailable.Profile;
        if (profile.AgentPort <= 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        {
            return ToolResult.Error(
                $"{unavailable.Message}{Environment.NewLine}"
                + $"主机 {profile.Name} 已配置备用端口 {profile.AgentPort}，但没有私钥。"
                + "备用通道使用主机配置里的私钥路径签名。");
        }

        try
        {
            var route = $"[{profile.Name}] 备用端口 {profile.AgentPort} 执行 {toolName}";
            if (unavailable.AlreadyKnown)
            {
                AuditLogger.Debug(route);
            }
            else
            {
                AuditLogger.Warn($"SSH 不可用，改走{route}");
            }

            var result = AgentConnectionPool.Send(profile, toolName, arguments);
            if (result.Ok)
            {
                // agentFirst 或冷却期内本来就不尝试 SSH，说成「SSH 不可用」会误导
                var note = unavailable.AlreadyKnown
                    ? $"（已通过备用端口 {profile.AgentPort} 完成本次操作）"
                    : $"（SSH 不可用，已通过备用端口 {profile.AgentPort} 完成本次操作）";
                return ToolResult.Ok(result.Text + Environment.NewLine + note);
            }

            return ToolResult.Error($"经端口 {profile.AgentPort}: {result.Text}");
        }
        catch (Exception exception)
        {
            AuditLogger.Error($"备用端口 {profile.AgentPort} 调用失败: {exception.Message}");
            return ToolResult.Error(
                $"{unavailable.Message}{Environment.NewLine}"
                + $"备用端口 {profile.AgentPort} 也不可用: {exception.Message}");
        }
    }

}
