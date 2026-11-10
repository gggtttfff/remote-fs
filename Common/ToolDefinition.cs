using System.Text.Json;
using RemoteFs.Mcp;

namespace RemoteFs.Common;

/// <summary>
/// MCP 工具定义：名称、描述、入参 Schema 与执行处理器。
/// </summary>
public sealed class ToolDefinition
{
    /// <summary>工具名称，全局唯一</summary>
    public required string Name { get; init; }

    /// <summary>工具描述，会作为提示词提供给模型，写清楚使用场景</summary>
    public required string Description { get; init; }

    /// <summary>入参 JSON Schema</summary>
    public required JsonElement InputSchema { get; init; }

    /// <summary>执行处理器</summary>
    public required Func<ToolCallContext, ToolResult> Handler { get; init; }
}
