using System.Net.Sockets;
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

        var token = profile.ResolveAgentToken();
        if (string.IsNullOrEmpty(token))
        {
            return ToolResult.Error(
                $"{unavailable.Message}{Environment.NewLine}"
                + $"主机 {profile.Name} 已配置备用端口 {profile.AgentPort}，但没有令牌。"
                + "请填写 agentToken，或设置 agentTokenEnvironmentVariable。");
        }

        try
        {
            AuditLogger.Warn(
                $"SSH 不可用 [{profile.Name}]，改走备用端口 {profile.AgentPort} 执行 {toolName}");
            var result = Invoke(profile, token, toolName, arguments);
            if (result.Ok)
            {
                return ToolResult.Ok(
                    result.Text
                    + $"{Environment.NewLine}（SSH 不可用，已通过端口 {profile.AgentPort} 完成本次操作）");
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

    private static (bool Ok, string Text) Invoke(
        HostProfile profile, string token, string toolName, JsonObject arguments)
    {
        using var client = new TcpClient { NoDelay = true };
        var timeout = TimeSpan.FromMilliseconds(Math.Max(profile.CommandTimeoutMilliseconds, 30_000));
        using var cancellation = new CancellationTokenSource(timeout);
        client.ConnectAsync(profile.Host, profile.AgentPort, cancellation.Token)
            .GetAwaiter()
            .GetResult();

        using var stream = client.GetStream();
        var request = new JsonObject
        {
            ["token"] = token,
            ["tool"] = toolName,
            ["arguments"] = arguments.DeepClone()
        };
        AgentFrame.WriteAsync(stream, request.ToJsonString(), cancellation.Token)
            .GetAwaiter()
            .GetResult();

        var responseJson = AgentFrame.ReadAsync(stream, cancellation.Token).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            throw new IOException("备用端口没有返回结果");
        }

        var response = JsonNode.Parse(responseJson) as JsonObject
            ?? throw new InvalidOperationException("备用端口返回的不是 JSON 对象");
        var ok = response["ok"]?.GetValue<bool>() ?? false;
        var text = response["text"]?.GetValue<string>() ?? string.Empty;
        return (ok, text);
    }
}
