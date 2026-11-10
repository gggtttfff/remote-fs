namespace RemoteFs.Common;

/// <summary>
/// 工具调用结果。IsError 为 true 时 MCP 协议仍返回成功响应，但内容标记为错误，便于模型自我纠正。
/// </summary>
public sealed class ToolResult
{
    public bool IsError { get; init; }

    public string Text { get; init; } = string.Empty;

    /// <summary>成功结果</summary>
    public static ToolResult Ok(string text) => new() { Text = text };

    /// <summary>失败结果</summary>
    public static ToolResult Error(string message) => new() { IsError = true, Text = message };
}
