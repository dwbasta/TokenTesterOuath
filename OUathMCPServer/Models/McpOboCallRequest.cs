using System.Text.Json;

namespace OUathMCPServer.Models;

public sealed class McpOboCallRequest
{
    public string Path { get; init; } = "/api/data";
    public string Method { get; init; } = "GET";
    public string? Scope { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public JsonElement? Body { get; init; }
}