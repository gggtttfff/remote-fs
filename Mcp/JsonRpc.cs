using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemoteFs.Mcp;

/// <summary>
/// JSON-RPC 2.0 消息模型
/// </summary>
public sealed class JsonRpcRequest
{
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

    public const int MethodInitialize = 0;

    /// <summary>服务器实现信息</summary>
    public static readonly JsonObject ServerInfo = new()
    {
        ["name"] = "remote-fs",
        ["version"] = "1.0.0"
    };
}
