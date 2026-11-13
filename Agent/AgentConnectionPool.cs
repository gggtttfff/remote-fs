using System.Net.Sockets;
using System.Text.Json.Nodes;
using RemoteFs.Common;
using RemoteFs.Config;

namespace RemoteFs.Agent;

/// <summary>
/// 按主机复用一条到备用端口的 TCP 连接。MCP 调用是串行的，同一条连接不会并发读写。
/// 对端如果仍是「一问一答就关闭」的旧版本，这次失败后会丢掉连接再连一次。
/// </summary>
internal static class AgentConnectionPool
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, PooledAgent> Connections = new(StringComparer.OrdinalIgnoreCase);

    public static (bool Ok, string Text) Send(
        HostProfile profile, string toolName, JsonObject arguments)
    {
        var key = $"{profile.Host}:{profile.AgentPort}";
        var request = new JsonObject
        {
            ["tool"] = toolName,
            ["arguments"] = arguments.DeepClone()
        };
        var json = request.ToJsonString();
        var timeout = TimeSpan.FromMilliseconds(Math.Max(profile.CommandTimeoutMilliseconds, 30_000));

        lock (Gate)
        {
            if (!Connections.TryGetValue(key, out var pooled))
            {
                pooled = new PooledAgent();
                Connections[key] = pooled;
            }

            try
            {
                return SendOnce(profile, pooled, json, timeout);
            }
            catch (AgentRequestExecutedException first)
            {
                // 请求已经完整发出，对端可能已经执行了。重发会让写操作重复执行，
                // 这里只把错误如实报回去，让调用方自己判断要不要重来。
                AuditLogger.Warn($"备用端口请求发出后未收到响应，不自动重发 {key}: {first.Message}");
                throw new IOException(
                    $"已向备用端口 {profile.AgentPort} 发出 {toolName}，但没收到响应。"
                    + "为避免重复执行没有自动重发，请先确认服务器上的结果再决定是否重试。",
                    first);
            }
            catch (Exception first)
            {
                // 请求还没完整发出，对端不会执行，重连一次是安全的
                AuditLogger.Info($"备用连接不可用，重新连接 {key}: {first.Message}");
                pooled.Dispose();
                return SendOnce(profile, pooled, json, timeout);
            }
        }
    }

    private static (bool Ok, string Text) SendOnce(
        HostProfile profile, PooledAgent pooled, string json, TimeSpan timeout)
    {
        EnsureConnected(profile, pooled);
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            AgentFrame.WriteAsync(pooled.Stream!, json, cancellation.Token).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 帧没写完整，对端读不到完整请求，不会执行
            pooled.Dispose();
            throw;
        }

        try
        {
            var responseJson = AgentFrame.ReadAsync(pooled.Stream!, cancellation.Token).GetAwaiter().GetResult();
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
        catch (Exception exception)
        {
            // 请求已经完整发出，对端可能已经执行完，不能自动重发
            pooled.Dispose();
            throw new AgentRequestExecutedException("读取响应失败", exception);
        }
    }

    private static void EnsureConnected(HostProfile profile, PooledAgent pooled)
    {
        if (IsAlive(pooled))
        {
            AuditLogger.Debug($"复用备用连接 {profile.Host}:{profile.AgentPort}");
            return;
        }

        pooled.Dispose();
        var client = new TcpClient { NoDelay = true };
        using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            client.ConnectAsync(profile.Host, profile.AgentPort, connect.Token).GetAwaiter().GetResult();
            var stream = client.GetStream();
            AgentHandshake.AuthenticateAsClient(
                stream, nonce => AgentSshIdentity.Sign(profile, nonce), connect.Token);
            pooled.Client = client;
            pooled.Stream = stream;
            AuditLogger.Info($"建立备用连接 {profile.Host}:{profile.AgentPort}");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 对端已经关掉连接时，Connected 仍可能是 true。可读且缓冲区为空就是关掉了。
    /// </summary>
    private static bool IsAlive(PooledAgent pooled)
    {
        if (pooled.Stream is null || pooled.Client is not { Connected: true })
        {
            return false;
        }

        try
        {
            var socket = pooled.Client.Client;
            return !(socket.Poll(0, System.Net.Sockets.SelectMode.SelectRead) && socket.Available == 0);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 请求已经完整发出但没收到响应。对端可能已经执行了，不能自动重发。
    /// </summary>
    private sealed class AgentRequestExecutedException : Exception
    {
        public AgentRequestExecutedException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    private sealed class PooledAgent : IDisposable
    {
        public TcpClient? Client { get; set; }

        public NetworkStream? Stream { get; set; }

        public void Dispose()
        {
            try
            {
                Stream?.Dispose();
            }
            catch (Exception)
            {
                // 连接已经坏了
            }

            try
            {
                Client?.Dispose();
            }
            catch (Exception)
            {
                // 同上
            }

            Stream = null;
            Client = null;
        }
    }
}
