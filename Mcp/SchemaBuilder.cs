using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemoteFs.Mcp;

/// <summary>
/// 工具入参 Schema 构造助手，简化常用类型定义
/// </summary>
public static class SchemaBuilder
{
    /// <summary>
    /// 构造对象 Schema
    /// </summary>
    /// <param name="properties">属性定义</param>
    /// <param name="required">必填属性名集合</param>
    public static JsonElement Object(
        IEnumerable<(string Name, JsonElement Schema)> properties,
        IEnumerable<string>? required = null)
    {
        var propertiesNode = new JsonObject();
        foreach (var (name, schema) in properties)
        {
            propertiesNode[name] = JsonNode.Parse(schema.GetRawText());
        }

        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = propertiesNode
        };

        var requiredList = required?.ToList();
        if (requiredList is { Count: > 0 })
        {
            root["required"] = new JsonArray(requiredList.Select(name => (JsonNode)name!).ToArray());
        }

        return JsonSerializer.Deserialize<JsonElement>(root.ToJsonString());
    }

    /// <summary>字符串类型 Schema</summary>
    public static JsonElement String(string description, string? defaultValue = null)
    {
        var node = new JsonObject
        {
            ["type"] = "string",
            ["description"] = description
        };
        if (defaultValue is not null)
        {
            node["default"] = defaultValue;
        }
        return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
    }

    /// <summary>整数类型 Schema</summary>
    public static JsonElement Integer(string description, int? minimum = null, int? maximum = null)
    {
        var node = new JsonObject
        {
            ["type"] = "integer",
            ["description"] = description
        };
        if (minimum is not null)
        {
            node["minimum"] = minimum;
        }
        if (maximum is not null)
        {
            node["maximum"] = maximum;
        }
        return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
    }

    /// <summary>布尔类型 Schema</summary>
    public static JsonElement Boolean(string description, bool defaultValue = false)
    {
        var node = new JsonObject
        {
            ["type"] = "boolean",
            ["description"] = description,
            ["default"] = defaultValue
        };
        return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
    }

    /// <summary>字符串数组 Schema</summary>
    public static JsonElement StringArray(string description)
    {
        var node = new JsonObject
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new JsonObject { ["type"] = "string" }
        };
        return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
    }
}
