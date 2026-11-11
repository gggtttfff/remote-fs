using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RemoteFs.Mcp;

/// <summary>
/// JSON-RPC 2.0 消息模型
/// </summary>
public sealed class JsonRpcRequest
{
    /// <summary>
    /// 协议字段必须全小写 jsonrpc，camelCase 会变成 jsonRpc 而被客户端判定为非法信封
    /// </summary>
    [JsonPropertyName("jsonrpc")]
    public string? JsonRpc { get; set; }

    /// <summary>请求 ID，通知请求为 null</summary>
    public JsonNode? Id { get; set; }

    public string? Method { get; set; }

    public JsonNode? Params { get; set; }
}

/// <summary>
/// JSON-RPC 响应
/// </summary>
public sealed class JsonRpcResponse
{
    /// <summary>
    /// 协议字段必须全小写 jsonrpc，写错客户端会直接丢弃整条响应
    /// </summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    public JsonNode? Id { get; set; }

    public JsonNode? Result { get; set; }

    public JsonRpcError? Error { get; set; }
}

/// <summary>
/// JSON-RPC 错误
/// </summary>
public sealed class JsonRpcError
{
    public int Code { get; set; }

    public string Message { get; set; } = string.Empty;

    public object? Data { get; set; }
}

/// <summary>
/// MCP 协议常量与版本号
/// </summary>
public static class McpProtocol
{
    public const string ProtocolVersion = "2025-06-18";

    /// <summary>
    /// 服务版本号，直接取自程序集，避免多处硬编码不同步
    /// </summary>
    public static string ServerVersion =>
        typeof(McpProtocol).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>服务器实现信息</summary>
    public static JsonObject BuildServerInfo() => new()
    {
        ["name"] = "remote-fs",
        ["version"] = ServerVersion
    };
}