using System.Text.Json.Nodes;
using RemoteFs.Common;

namespace RemoteFs.Mcp;

/// <summary>
/// MCP 工具调用上下文：封装入参读取，避免每个工具重复做类型转换与缺失校验。
/// </summary>
public sealed class ToolCallContext
{
    private readonly JsonObject _arguments;

    public ToolCallContext(JsonNode? arguments)
    {
        _arguments = arguments as JsonObject ?? new JsonObject();
    }

    /// <summary>读取必填字符串参数，空白视为缺失</summary>
    public string RequireString(string name)
    {
        var value = GetString(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"参数 '{name}' 为必填项且不能为空");
        }
        return value;
    }

    /// <summary>
    /// 读取必填文本参数，允许空字符串。
    /// 用于写文件这类「空内容也是合法值」的场景，与 RequireString 区分开。
    /// </summary>
    public string RequireText(string name)
    {
        if (!_arguments.TryGetPropertyValue(name, out var node) || node is null)
        {
            throw new ArgumentException($"参数 '{name}' 为必填项");
        }
        return ReadStringValue(node) ?? string.Empty;
    }

    /// <summary>读取可选字符串参数</summary>
    public string? GetString(string name)
    {
        if (!_arguments.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }
        var value = ReadStringValue(node);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>读取可选字符串参数，缺省时返回默认值</summary>
    public string GetStringOrDefault(string name, string defaultValue)
    {
        return GetString(name) ?? defaultValue;
    }

    /// <summary>读取可选整数参数</summary>
    public int? GetInt(string name)
    {
        if (!_arguments.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }
        try
        {
            return node.GetValue<int>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>读取可选长整数参数</summary>
    public long? GetLong(string name)
    {
        if (!_arguments.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }
        try
        {
            return node.GetValue<long>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>读取可选布尔参数</summary>
    public bool GetBool(string name, bool defaultValue = false)
    {
        if (!_arguments.TryGetPropertyValue(name, out var node) || node is null)
        {
            return defaultValue;
        }
        try
        {
            return node.GetValue<bool>();
        }
        catch (Exception)
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// 把节点读成字符串，非字符串类型（如数字、布尔）也能兼容转换
    /// </summary>
    private static string? ReadStringValue(JsonNode node)
    {
        // 非标量节点无法当字符串使用，按缺失处理而不是抛异常
        if (node is not System.Text.Json.Nodes.JsonValue)
        {
            return null;
        }

        var value = node.GetValue<System.Text.Json.JsonElement>();
        return value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }
}