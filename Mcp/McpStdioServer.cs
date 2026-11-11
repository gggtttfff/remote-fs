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
    private readonly Func<string?>? _hostResolver;
    private readonly Action<string, string?>? _onMutatingTool;

    public McpStdioServer(
        IReadOnlyList<ToolDefinition> tools,
        Func<string?>? hostResolver = null,
        Action<string, string?>? onMutatingTool = null)
    {
        _tools = tools;
        _hostResolver = hostResolver;
        _onMutatingTool = onMutatingTool;
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

            // 排查握手问题时能把客户端发来的原始报文打出来
            AuditLogger.Debug($"<- {Shorten(line)}");

            await HandleMessageAsync(line, writer, cancellationToken);
        }

        AuditLogger.Info("MCP stdio 服务已停止");
    }

    /// <summary>
    /// 处理单条 JSON-RPC 消息
    /// </summary>
    private async Task HandleMessageAsync(string line, StreamWriter writer, CancellationToken cancellationToken)
    {
        JsonRpcRequest? request;
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
            var result = Dispatch(request);
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
    /// 分发请求到对应处理器。
    /// 工具处理逻辑是同步的（SFTP 连接不可并发访问），这里串行执行，不额外开线程池任务。
    /// </summary>
    private JsonNode? Dispatch(JsonRpcRequest request)
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
                    ["serverInfo"] = McpProtocol.BuildServerInfo()
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
                return CallTool(request.Params);

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
    private JsonNode CallTool(JsonNode? parameters)
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

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ToolResult result;
        string? errorDetail = null;
        try
        {
            result = tool.Handler(context);
            if (result.IsError)
            {
                errorDetail = result.Text;
            }
        }
        catch (Exception exception)
        {
            // 异常统一转成错误结果，让模型能读到原因并自行纠正
            AuditLogger.Error($"工具 {toolName} 执行失败: {exception}");
            errorDetail = exception.ToString();
            result = ToolResult.Error($"工具 {toolName} 执行失败: {exception.Message}");
        }
        stopwatch.Stop();

        // 1. 记录本次调用的完整出入参与耗时，是排查问题的主要依据
        var hostName = ResolveHostName(arguments);
        AuditLogger.ToolCall(
            toolName,
            hostName,
            arguments.ToJsonString(),
            result.Text,
            stopwatch.ElapsedMilliseconds,
            !result.IsError,
            errorDetail);

        // 2. 非只读工具可能改动远程内容，让它的目录缓存失效
        if (tool.Annotations?.ReadOnlyHint != true && hostName is not null)
        {
            try
            {
                _onMutatingTool?.Invoke(toolName, hostName);
            }
            catch (Exception exception)
            {
                AuditLogger.Warn($"失效缓存失败: {exception.Message}");
            }
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
    /// 取出入参里的主机别名，用于日志归类。
    /// 未显式传 host 时回落到默认主机，避免日志里主机一栏是空的。
    /// </summary>
    private string? ResolveHostName(JsonObject arguments)
    {
        if (arguments.TryGetPropertyValue("host", out var node) && node is not null)
        {
            try
            {
                var explicitHost = node.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(explicitHost))
                {
                    return explicitHost;
                }
            }
            catch (Exception)
            {
                // 解析失败按未指定处理
            }
        }

        try
        {
            return _hostResolver?.Invoke();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 工具定义转 JSON
    /// </summary>
    private JsonObject ToJson(ToolDefinition tool)
    {
        var node = new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["inputSchema"] = JsonSerializer.SerializeToNode(tool.InputSchema)
        };

        if (tool.Annotations is not null)
        {
            node["annotations"] = JsonSerializer.SerializeToNode(tool.Annotations, _jsonOptions);
        }

        return node;
    }

    /// <summary>
    /// 截断过长的报文，避免日志被整包内容撑爆
    /// </summary>
    private static string Shorten(string text)
    {
        return text.Length <= 300 ? text : text[..300] + $"...(共 {text.Length} 字符)";
    }

    /// <summary>
    /// 写出响应并刷盘
    /// </summary>
    private async Task WriteAsync(StreamWriter writer, JsonRpcResponse response, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(response, _jsonOptions);
        AuditLogger.Debug($"-> {Shorten(json)}");
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }
}