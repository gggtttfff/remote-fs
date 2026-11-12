using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using RemoteFs.Common;
using RemoteFs.Config;

namespace RemoteFs.Agent;

/// <summary>
/// 在服务器本机监听备用端口。Windows 上的 remote-fs 只在 SSH 连不上时才会连过来。
/// </summary>
public static class AgentListener
{
    public static async Task<int> RunAsync(AppConfig config, int port, CancellationToken cancellationToken)
    {
        var token = ResolveListenToken(config);
        if (string.IsNullOrEmpty(token))
        {
            Console.Error.WriteLine(
                "拒绝监听：没有备用通道令牌。请设置环境变量 REMOTE_FS_AGENT_TOKEN，或在 config.json 写入 agentListenToken。");
            return 1;
        }

        if (config.AgentAllowedRoots.Count == 0)
        {
            Console.Error.WriteLine(
                "拒绝监听：agentAllowedRoots 为空。请在 config.json 写明允许修改的目录，例如 [\"/var/www\"]。");
            return 1;
        }

        if (port is < 1 or > 65535)
        {
            Console.Error.WriteLine($"端口 {port} 不在 1-65535 之间");
            return 1;
        }

        var executor = new AgentToolExecutor(config);
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"备用通道已监听 0.0.0.0:{port}");
        Console.WriteLine("允许目录: " + string.Join(", ", config.AgentAllowedRoots));
        AuditLogger.Info($"备用通道已监听 0.0.0.0:{port}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // 每条连接独立处理，一台客户端卡住不影响下一次调用
                _ = Task.Run(() => HandleClient(client, token, executor, cancellationToken));
            }
        }
        finally
        {
            listener.Stop();
        }

        return 0;
    }

    /// <summary>命令行里的 --port 优先，其次用配置，都没有则用 43336。</summary>
    public static int ResolvePort(AppConfig config, string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] is "--port" or "-p" && index + 1 < args.Length
                && int.TryParse(args[index + 1], out var port))
            {
                return port;
            }
        }

        return config.AgentListenPort > 0 ? config.AgentListenPort : 43336;
    }

    private static string? ResolveListenToken(AppConfig config)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("REMOTE_FS_AGENT_TOKEN");
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        return string.IsNullOrEmpty(config.AgentListenToken) ? null : config.AgentListenToken;
    }

    private static void HandleClient(
        TcpClient client, string expectedToken, AgentToolExecutor executor, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));

                var requestJson = AgentFrame.ReadAsync(stream, timeout.Token).GetAwaiter().GetResult();
                if (string.IsNullOrWhiteSpace(requestJson))
                {
                    return;
                }

                var request = JsonNode.Parse(requestJson) as JsonObject;
                var provided = request?["token"]?.GetValue<string>() ?? string.Empty;
                if (request is null || !AgentToolExecutor.TokenMatches(expectedToken, provided))
                {
                    WriteResponse(stream, false, "令牌不正确", timeout.Token);
                    AuditLogger.Warn("备用通道拒绝了一次令牌不匹配的连接");
                    return;
                }

                var tool = request["tool"]?.GetValue<string>() ?? string.Empty;
                var arguments = request["arguments"] as JsonObject ?? new JsonObject();
                string? path = null;
                try
                {
                    path = arguments["path"]?.GetValue<string>();
                }
                catch (Exception)
                {
                    path = null;
                }
                AuditLogger.Info($"备用通道 {tool} {path}");

                var (ok, text) = executor.Execute(tool, arguments);
                WriteResponse(stream, ok, text, timeout.Token);
            }
            catch (Exception exception)
            {
                AuditLogger.Warn($"备用通道处理连接失败: {exception.Message}");
                try
                {
                    using var stream = client.GetStream();
                    WriteResponse(stream, false, exception.Message, CancellationToken.None);
                }
                catch (Exception)
                {
                    // 连接已经断了，没有地方可写
                }
            }
        }
    }

    private static void WriteResponse(Stream stream, bool ok, string text, CancellationToken cancellationToken)
    {
        var response = new JsonObject
        {
            ["ok"] = ok,
            ["text"] = text
        };
        AgentFrame.WriteAsync(stream, response.ToJsonString(), cancellationToken).GetAwaiter().GetResult();
    }
}
