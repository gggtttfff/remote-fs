using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteFs.Common;

namespace RemoteFs.Mcp;

/// <summary>
/// MCP 工具调用上下文：封装入参读取，避免每个工具重复做类型转换与缺失校验。
/// </summary>
public sealed class ToolCallContext
{
    private readonly System.Text.Json.Nodes.JsonObject _arguments;

    public ToolCallContext(JsonNode? arguments)
    {
        _arguments = arguments as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
    }

    /// <summary>读取必填字符串参数</summary>
    public string RequireString(string name)
    {
        var value = GetString(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"参数 '{name}' 为必填项且不能为空");
        }
        return value;
    }

    /// <summary>读取可选字符串参数</summary>
    public string? GetString(string name)
    {
        if (!_arguments.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }
        var value = node.GetValue<string>();
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

    /// <summary>读取可选字符串数组参数</summary>
    public List<string> GetStringList(string name)
    {
        var result = new List<string>();
        if (!_arguments.TryGetPropertyValue(name, out var node) || node is null)
        {
            return result;
        }
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    result.Add(item.GetValue<string>());
                }
            }
        }
        return result;
    }
}
