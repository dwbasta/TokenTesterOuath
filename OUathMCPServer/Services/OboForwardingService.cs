using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OUathMCPServer.Models;

namespace OUathMCPServer.Services;

public sealed class OboForwardingService(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<OboForwardingService> logger)
{
    public async Task<OboForwardResult> ForwardAsync(
        string userAccessToken,
        McpOboCallRequest request,
        CancellationToken cancellationToken)
    {
        var authority = configuration["Jwt:Authority"];
        var clientId = configuration["Obo:ClientId"];
        var clientSecret = configuration["Obo:ClientSecret"];
        var downstreamBaseUrl = configuration["Obo:DownstreamApiBaseUrl"];
        var downstreamScope = request.Scope ?? configuration["Obo:DownstreamScope"];

        if (string.IsNullOrWhiteSpace(authority) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret) ||
            string.IsNullOrWhiteSpace(downstreamBaseUrl) ||
            string.IsNullOrWhiteSpace(downstreamScope))
        {
            throw new InvalidOperationException("OBO configuration is incomplete.");
        }

        if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri))
        {
            throw new InvalidOperationException("Jwt:Authority must be a valid absolute URI.");
        }

        var authorityPathSegments = authorityUri.AbsolutePath
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (authorityPathSegments.Length == 0)
        {
            throw new InvalidOperationException("Jwt:Authority must include a tenant segment.");
        }

        var tenantId = authorityPathSegments[0];

        if (!Uri.TryCreate(downstreamBaseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("Obo:DownstreamApiBaseUrl must be a valid absolute URI.");
        }

        if (Uri.TryCreate(request.Path, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Path must be relative.");
        }

        var tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["requested_token_use"] = "on_behalf_of",
                ["assertion"] = userAccessToken,
                ["scope"] = downstreamScope
            })
        };

        using var tokenResponse = await httpClient.SendAsync(tokenRequest, cancellationToken);
        var tokenPayload = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            logger.LogWarning("OBO token request failed: {Code} {Payload}", tokenResponse.StatusCode, tokenPayload);
            throw new HttpRequestException($"OBO token request failed with status code {(int)tokenResponse.StatusCode}.");
        }

        using var tokenJson = JsonDocument.Parse(tokenPayload);
        if (!tokenJson.RootElement.TryGetProperty("access_token", out var accessTokenElement))
        {
            throw new InvalidOperationException("OBO token response does not include access_token.");
        }

        var accessToken = accessTokenElement.GetString();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("OBO token response contained an empty access token.");
        }

        var method = ParseMethod(request.Method);
        var downstreamUri = new Uri(baseUri, request.Path);
        using var downstreamRequest = new HttpRequestMessage(method, downstreamUri);
        downstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        if (request.Headers is not null)
        {
            foreach (var header in request.Headers)
            {
                if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                downstreamRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (request.Body.HasValue && AllowsBody(method))
        {
            downstreamRequest.Content = new StringContent(request.Body.Value.GetRawText(), Encoding.UTF8, "application/json");
        }

        using var downstreamResponse = await httpClient.SendAsync(downstreamRequest, cancellationToken);
        var responseBody = await downstreamResponse.Content.ReadAsStringAsync(cancellationToken);
        var contentType = downstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/json";

        return new OboForwardResult((int)downstreamResponse.StatusCode, contentType, responseBody);
    }

    private static HttpMethod ParseMethod(string method) => method.Trim().ToUpperInvariant() switch
    {
        "GET" => HttpMethod.Get,
        "POST" => HttpMethod.Post,
        "PUT" => HttpMethod.Put,
        "PATCH" => HttpMethod.Patch,
        "DELETE" => HttpMethod.Delete,
        _ => throw new InvalidOperationException("Unsupported method. Allowed: GET, POST, PUT, PATCH, DELETE.")
    };

    private static bool AllowsBody(HttpMethod method) =>
        method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch;
}