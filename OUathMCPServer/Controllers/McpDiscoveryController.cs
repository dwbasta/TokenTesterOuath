using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OUathMCPServer.Models;
using OUathMCPServer.Services;

namespace OUathMCPServer.Controllers;

[ApiController]
public sealed class McpDiscoveryController(
    IConfiguration configuration,
    OboForwardingService oboForwardingService,
    ILogger<McpDiscoveryController> logger) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("/.well-known/mcp")]
    public IActionResult GetMcpDiscovery()
    {
        var host = $"{Request.Scheme}://{Request.Host}";
        var metadata = GetAuthorityMetadataSafe();
        var requiredScope = configuration["Jwt:RequiredScope"] ?? "access_as_user";
        var audiences = configuration.GetSection("Jwt:Audiences").Get<string[]>() ?? Array.Empty<string>();

        return Ok(new
        {
            name = "TokenTesterMCPServer",
            version = "1.0.0",
            endpoints = new { mcp = $"{host}/mcp" },
            authentication = new
            {
                type = "oauth2",
                issuer = metadata.Issuer,
                authorization_servers = new[] { metadata.Issuer },
                authorization_endpoint = metadata.AuthorizationEndpoint,
                token_endpoint = metadata.TokenEndpoint,
                jwks_uri = metadata.JwksUri,
                scopes_supported = new[] { requiredScope },
                audiences
            },
            capabilities = new { tools = new { } }
        });
    }

    [AllowAnonymous]
    [HttpGet("/mcp")]
    public IActionResult GetMcpEndpointInfo()
    {
        return Ok(new
        {
            name = "TokenTesterMCPServer",
            endpoint = "/mcp",
            transport = "http-jsonrpc"
        });
    }

    [AllowAnonymous]
    [HttpGet("/.well-known/oauth-protected-resource")]
    public IActionResult GetOAuthProtectedResourceRoot() => Ok(BuildProtectedResourceMetadata());

    [AllowAnonymous]
    [HttpGet("/.well-known/oauth-protected-resource/{*resourcePath}")]
    public IActionResult GetOAuthProtectedResourceForPath(string? resourcePath) => Ok(BuildProtectedResourceMetadata());

    [AllowAnonymous]
    [HttpGet("/.well-known/oauth-authorization-server")]
    public IActionResult GetOAuthAuthorizationServerMetadataRoot() => Ok(BuildAuthorizationServerMetadata());

    [AllowAnonymous]
    [HttpGet("/.well-known/oauth-authorization-server/{*resourcePath}")]
    public IActionResult GetOAuthAuthorizationServerMetadataForPath(string? resourcePath) => Ok(BuildAuthorizationServerMetadata());

    [AllowAnonymous]
    [HttpGet("/.well-known/openid-configuration")]
    public IActionResult GetOpenIdConfigurationRoot() => Ok(BuildOpenIdMetadata());

    [AllowAnonymous]
    [HttpGet("/.well-known/openid-configuration/{*resourcePath}")]
    public IActionResult GetOpenIdConfigurationForPath(string? resourcePath) => Ok(BuildOpenIdMetadata());

    [AllowAnonymous]
    [HttpPost("/mcp")]
    public async Task<IActionResult> PostMcpAsync([FromBody] JsonRpcRequest? request, CancellationToken cancellationToken)
    {
        var responseId = request?.Id?.Clone();

        if (request is null || string.IsNullOrWhiteSpace(request.Method))
        {
            return Ok(JsonRpcError(responseId, -32600, "Invalid Request"));
        }

        try
        {
            return request.Method switch
            {
                "initialize" => Ok(JsonRpcResult(responseId, new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "TokenTesterMCPServer", version = "1.0.0" }
                })),
                "tools/list" => Ok(JsonRpcResult(responseId, new
                {
                    tools = new object[]
                    {
                        new
                        {
                            name = "invoke_obo_call",
                            description = "Performs OBO token exchange and calls downstream API.",
                            inputSchema = new
                            {
                                type = "object",
                                required = new[] { "path", "method" },
                                properties = new
                                {
                                    path = new { type = "string" },
                                    method = new { type = "string", @enum = new[] { "GET", "POST", "PUT", "PATCH", "DELETE" } },
                                    headers = new { type = "object", additionalProperties = new { type = "string" } },
                                    body = new { type = "object" },
                                    scope = new { type = "string" }
                                }
                            }
                        }
                    }
                })),
                "tools/call" => await CallToolAsync(responseId, request, cancellationToken),
                _ => Ok(JsonRpcError(responseId, -32601, $"Method not found: {request.Method}"))
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MCP method failed: {Method}", request.Method);
            return Ok(JsonRpcError(responseId, -32000, ex.Message));
        }
    }

    private object BuildProtectedResourceMetadata()
    {
        var metadata = GetAuthorityMetadataSafe();
        var requiredScope = configuration["Jwt:RequiredScope"] ?? "access_as_user";
        var audiences = configuration.GetSection("Jwt:Audiences").Get<string[]>() ?? Array.Empty<string>();
        var resource = audiences.FirstOrDefault() ?? $"{Request.Scheme}://{Request.Host}";

        return new
        {
            resource,
            authorization_servers = new[] { metadata.Issuer },
            scopes_supported = new[] { requiredScope },
            bearer_methods_supported = new[] { "header" }
        };
    }

    private object BuildAuthorizationServerMetadata()
    {
        var metadata = GetAuthorityMetadataSafe();
        return new
        {
            issuer = metadata.Issuer,
            authorization_endpoint = metadata.AuthorizationEndpoint,
            token_endpoint = metadata.TokenEndpoint,
            jwks_uri = metadata.JwksUri,
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token", "urn:ietf:params:oauth:grant-type:jwt-bearer" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_post", "client_secret_basic" },
            scopes_supported = new[] { configuration["Jwt:RequiredScope"] ?? "access_as_user" }
        };
    }

    private object BuildOpenIdMetadata()
    {
        var metadata = GetAuthorityMetadataSafe();
        return new
        {
            issuer = metadata.Issuer,
            authorization_endpoint = metadata.AuthorizationEndpoint,
            token_endpoint = metadata.TokenEndpoint,
            jwks_uri = metadata.JwksUri,
            response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "pairwise" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
            scopes_supported = new[] { configuration["Jwt:RequiredScope"] ?? "access_as_user", "openid", "profile", "offline_access" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_post", "client_secret_basic" }
        };
    }

    private AuthorityMetadata GetAuthorityMetadataSafe()
    {
        var authority = configuration["Jwt:Authority"] ?? string.Empty;
        if (Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri))
        {
            var segments = authorityUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0)
            {
                var tenantId = segments[0];
                var baseUrl = $"https://login.microsoftonline.com/{tenantId}";
                return new AuthorityMetadata(
                    $"{baseUrl}/v2.0",
                    $"{baseUrl}/oauth2/v2.0/authorize",
                    $"{baseUrl}/oauth2/v2.0/token",
                    $"{baseUrl}/discovery/v2.0/keys");
            }
        }

        var fallback = "https://login.microsoftonline.com/common";
        return new AuthorityMetadata(
            $"{fallback}/v2.0",
            $"{fallback}/oauth2/v2.0/authorize",
            $"{fallback}/oauth2/v2.0/token",
            $"{fallback}/discovery/v2.0/keys");
    }

    private async Task<IActionResult> CallToolAsync(JsonElement? responseId, JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (request.Params.ValueKind != JsonValueKind.Object ||
            !request.Params.TryGetProperty("name", out var nameElement) ||
            !string.Equals(nameElement.GetString(), "invoke_obo_call", StringComparison.Ordinal) ||
            !request.Params.TryGetProperty("arguments", out var argumentsElement) ||
            argumentsElement.ValueKind != JsonValueKind.Object)
        {
            return Ok(JsonRpcError(responseId, -32602, "Invalid tool call arguments."));
        }

        if (!argumentsElement.TryGetProperty("path", out var pathElement) || string.IsNullOrWhiteSpace(pathElement.GetString()) ||
            !argumentsElement.TryGetProperty("method", out var methodElement) || string.IsNullOrWhiteSpace(methodElement.GetString()))
        {
            return Ok(JsonRpcError(responseId, -32602, "Arguments path and method are required."));
        }

        if (!Request.Headers.TryGetValue("Authorization", out var authorizationHeader) ||
            !AuthenticationHeaderValue.TryParse(authorizationHeader.ToString(), out var headerValue) ||
            !string.Equals(headerValue.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(headerValue.Parameter))
        {
            return Ok(JsonRpcError(responseId, -32001, "Missing bearer token."));
        }

        Dictionary<string, string>? headers = null;
        if (argumentsElement.TryGetProperty("headers", out var headersElement) && headersElement.ValueKind == JsonValueKind.Object)
        {
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in headersElement.EnumerateObject())
            {
                headers[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        JsonElement? body = null;
        if (argumentsElement.TryGetProperty("body", out var bodyElement))
        {
            body = bodyElement.Clone();
        }

        string? scope = null;
        if (argumentsElement.TryGetProperty("scope", out var scopeElement) && scopeElement.ValueKind == JsonValueKind.String)
        {
            scope = scopeElement.GetString();
        }

        var oboRequest = new McpOboCallRequest
        {
            Path = pathElement.GetString()!,
            Method = methodElement.GetString()!,
            Headers = headers,
            Body = body,
            Scope = scope
        };

        var result = await oboForwardingService.ForwardAsync(headerValue.Parameter!, oboRequest, cancellationToken);
        return Ok(JsonRpcResult(responseId, new
        {
            content = new[] { new { type = "text", text = result.Body } },
            isError = result.StatusCode >= 400
        }));
    }

    private static object JsonRpcResult(JsonElement? id, object result) => new { jsonrpc = "2.0", id, result };
    private static object JsonRpcError(JsonElement? id, int code, string message) => new { jsonrpc = "2.0", id, error = new { code, message } };

    public sealed class JsonRpcRequest
    {
        public string Jsonrpc { get; init; } = "2.0";
        public string Method { get; init; } = string.Empty;
        public JsonElement? Id { get; init; }
        public JsonElement Params { get; init; }
    }

    private sealed record AuthorityMetadata(string Issuer, string AuthorizationEndpoint, string TokenEndpoint, string JwksUri);
}