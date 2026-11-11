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

    /// <summary>行为提示，供客户端判断是否需要二次确认</summary>
    public ToolAnnotations? Annotations { get; init; }

    /// <summary>执行处理器</summary>
    public required Func<ToolCallContext, ToolResult> Handler { get; init; }
}

/// <summary>
/// MCP 工具注解：让客户端能区分只读工具与破坏性工具。
/// </summary>
public sealed class ToolAnnotations
{
    public bool? ReadOnlyHint { get; init; }

    public bool? DestructiveHint { get; init; }

    public bool? IdempotentHint { get; init; }

    /// <summary>只读查询，重复执行无副作用</summary>
    public static ToolAnnotations ReadOnly { get; } = new()
    {
        ReadOnlyHint = true,
        DestructiveHint = false,
        IdempotentHint = true
    };

    /// <summary>会修改内容，重复执行结果一致</summary>
    public static ToolAnnotations Modifying { get; } = new()
    {
        ReadOnlyHint = false,
        DestructiveHint = false,
        IdempotentHint = true
    };

    /// <summary>可能删除或覆盖数据，客户端应提示用户确认</summary>
    public static ToolAnnotations Destructive { get; } = new()
    {
        ReadOnlyHint = false,
        DestructiveHint = true,
        IdempotentHint = false
    };
}