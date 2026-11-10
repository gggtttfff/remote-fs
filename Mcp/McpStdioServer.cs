using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteFs.Common;

namespace RemoteFs.Mcp;

/// <summary>
/// MCP stdio 服务端：按行读取 JSON-RPC 消息，写回响应。
/// stdout 专用于协议消息，日志一律走 stderr。
/// </summary>
public sealed class McpStdioServer
{
    private readonly IReadOnlyList<ToolDefinition> _tools;
    private readonly JsonSerializerOptions _jsonOptions;

    public McpStdioServer(IReadOnlyList<ToolDefinition> tools)
    {
        _tools = tools;
        _jsonOptions = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // MCP 协议字段为小写（jsonrpc/id/result/error），序列化与反序列化都按 camelCase
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            // 空值不输出，避免出现 "error": null 这类干扰客户端的字段
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// 启动服务主循环，直到 stdin 关闭
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        AuditLogger.Info("MCP stdio 服务已启动");

        var reader = new StreamReader(
            Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
        // 不写 BOM，否则客户端解析首条消息会失败
        var writer = new StreamWriter(
            Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false))
        {
            AutoFlush = false,
            NewLine = "\n"
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                AuditLogger.Info("stdin 已关闭 (EOF)");
                break;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            await HandleMessageAsync(line, writer, cancellationToken);
        }

        AuditLogger.Info("MCP stdio 服务已停止");
    }

    /// <summary>
    /// 处理单条 JSON-RPC 消息
    /// </summary>
    private async Task HandleMessageAsync(string line, StreamWriter writer, CancellationToken cancellationToken)
    {
        JsonRpcRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<JsonRpcRequest>(line, _jsonOptions);
        }
        catch (Exception exception)
        {
            AuditLogger.Error($"消息解析失败: {exception.Message}");
            await WriteAsync(writer, new JsonRpcResponse
            {
                Id = null,
                Error = new JsonRpcError { Code = -32700, Message = "Parse error: 请求不是合法 JSON" }
            }, cancellationToken);
            return;
        }

        if (request?.Method is null)
        {
            return;
        }

        var isNotification = request.Id is null;
        try
        {
            var result = await DispatchAsync(request, cancellationToken);
            if (!isNotification && result is not null)
            {
                await WriteAsync(writer, new JsonRpcResponse { Id = request.Id, Result = result }, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            AuditLogger.Error($"处理 {request.Method} 失败: {exception}");
            if (!isNotification)
            {
                await WriteAsync(writer, new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError { Code = -32603, Message = exception.Message }
                }, cancellationToken);
            }
        }
    }

    /// <summary>
    /// 分发请求到对应处理器
    /// </summary>
    private async Task<JsonNode?> DispatchAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case "initialize":
                return new JsonObject
                {
                    ["protocolVersion"] = McpProtocol.ProtocolVersion,
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject { ["listChanged"] = false }
                    },
                    ["serverInfo"] = McpProtocol.ServerInfo
                };

            case "notifications/initialized":
            case "notifications/cancelled":
                // 通知无需响应
                return null;

            case "ping":
                return new JsonObject();

            case "tools/list":
                return new JsonObject
                {
                    ["tools"] = new JsonArray(_tools.Select(ToJson).ToArray())
                };

            case "tools/call":
                return await CallToolAsync(request.Params, cancellationToken);

            default:
                if (request.Id is null)
                {
                    // 未知通知直接忽略
                    return null;
                }
                throw new InvalidOperationException($"不支持的方法: {request.Method}");
        }
    }

    /// <summary>
    /// 执行工具调用
    /// </summary>
    private async Task<JsonNode> CallToolAsync(JsonNode? parameters, CancellationToken cancellationToken)
    {
        if (parameters is null)
        {
            throw new InvalidOperationException("tools/call 缺少 params");
        }

        var toolName = parameters["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(toolName))
        {
            throw new InvalidOperationException("tools/call 缺少工具名");
        }

        var tool = _tools.FirstOrDefault(item => item.Name == toolName);
        if (tool is null)
        {
            throw new InvalidOperationException($"未知工具: {toolName}");
        }

        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        var context = new ToolCallContext(arguments);

        ToolResult result;
        try
        {
            result = await Task.Run(() => tool.Handler(context), cancellationToken);
        }
        catch (Exception exception)
        {
            // 异常统一转成错误结果，让模型能读到原因并自行纠正
            AuditLogger.Error($"工具 {toolName} 执行失败: {exception}");
            result = ToolResult.Error($"工具 {toolName} 执行失败: {exception.Message}");
        }

        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = result.Text
            }),
            ["isError"] = result.IsError
        };
    }

    /// <summary>
    /// 工具定义转 JSON
    /// </summary>
    private JsonObject ToJson(ToolDefinition tool)
    {
        return new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["inputSchema"] = JsonNode.Parse(tool.InputSchema.GetRawText())
        };
    }

    /// <summary>
    /// 写出响应并刷盘
    /// </summary>
    private async Task WriteAsync(StreamWriter writer, JsonRpcResponse response, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(response, _jsonOptions);
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }
}
