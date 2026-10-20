using System.Text.Json;
using System.Text.Json.Serialization;
using ActivityExplorer.Mcp.Models;
using ModelContextProtocol.Protocol;

namespace ActivityExplorer.Mcp;

public static class ToolResultFactory
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    public static CallToolResult Create<T>(T? data, ToolError? error, DateTimeOffset asOfUtc)
    {
        var json = JsonSerializer.SerializeToElement(new ToolEnvelope<T>(data, error, asOfUtc), JsonOptions);
        return new() { StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }], IsError = error is not null };
    }

    public static CallToolResult Error(string code, string message, DateTimeOffset asOfUtc, bool retryable = false) =>
        Create<object>(null, new(code, message, retryable), asOfUtc);
}
