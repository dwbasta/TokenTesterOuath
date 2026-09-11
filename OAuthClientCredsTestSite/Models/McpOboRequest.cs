using System.Text.Json;

namespace OAuthClientCredsTestSite.Models;

public sealed class McpOboRequest
{
    public string Path { get; init; } = "/";
    public string HttpMethod { get; init; } = "GET";
    public string? Scope { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public JsonElement? Body { get; init; }
}