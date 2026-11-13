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
        IReadOnlyList<Renci.SshNet.Security.Key> authorizedKeys;
        try
        {
            authorizedKeys = AgentSshIdentity.LoadAuthorizedKeys(config.AgentAuthorizedKeysPath);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"拒绝监听：{exception.Message}");
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
                _ = Task.Run(() => HandleClient(client, authorizedKeys, executor, cancellationToken));
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

    private static void HandleClient(
        TcpClient client,
        IReadOnlyList<Renci.SshNet.Security.Key> authorizedKeys,
        AgentToolExecutor executor,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(TimeSpan.FromMinutes(2));
                if (!AgentHandshake.AuthenticateAsServer(stream, authorizedKeys, idle.Token))
                {
                    AuditLogger.Warn("备用通道公钥认证失败");
                    return;
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    using var requestIdle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    requestIdle.CancelAfter(TimeSpan.FromMinutes(10));
                    string? requestJson;
                    try
                    {
                        requestJson = AgentFrame.ReadAsync(stream, requestIdle.Token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        AuditLogger.Info("备用通道空闲，关闭这条连接");
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(requestJson))
                    {
                        return;
                    }

                    var request = JsonNode.Parse(requestJson) as JsonObject;
                    if (request is null)
                    {
                        WriteResponse(stream, false, "请求不是 JSON 对象", requestIdle.Token);
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

                    // 1.1 回包不能用读请求的令牌：remote_exec 上限 60 分钟，
                    // 远超这 10 分钟，令牌过期后回包会抛异常，客户端永远等不到响应
                    var (ok, text) = executor.Execute(tool, arguments);
                    using var responseWrite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    responseWrite.CancelAfter(TimeSpan.FromMinutes(2));
                    WriteResponse(stream, ok, text, responseWrite.Token);
                }
            }
            catch (Exception exception)
            {
                AuditLogger.Warn($"备用通道处理连接失败: {exception.Message}");
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
