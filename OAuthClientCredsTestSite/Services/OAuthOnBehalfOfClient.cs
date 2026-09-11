using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OAuthClientCredsTestSite.Models;

namespace OAuthClientCredsTestSite.Services;

public sealed class OAuthOnBehalfOfClient(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<OAuthOnBehalfOfClient> logger)
{
    public async Task<OboDownstreamResponse> ForwardAsync(
        string userAccessToken,
        McpOboRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = configuration["Obo:TenantId"];
        var clientId = configuration["Obo:ClientId"];
        var clientSecret = configuration["Obo:ClientSecret"];
        var downstreamScope = request.Scope ?? configuration["Obo:DownstreamScope"];
        var downstreamBaseUrl = configuration["Obo:DownstreamApiBaseUrl"];

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret) ||
            string.IsNullOrWhiteSpace(downstreamScope) ||
            string.IsNullOrWhiteSpace(downstreamBaseUrl))
        {
            throw new InvalidOperationException("OBO configuration is incomplete. Set Obo:TenantId, Obo:ClientId, Obo:ClientSecret, Obo:DownstreamScope, and Obo:DownstreamApiBaseUrl.");
        }

        if (!Uri.TryCreate(downstreamBaseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("Obo:DownstreamApiBaseUrl must be a valid absolute URI.");
        }

        if (Uri.TryCreate(request.Path, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Request path must be relative.");
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
            logger.LogWarning("OBO token exchange failed: {StatusCode} {Payload}", tokenResponse.StatusCode, tokenPayload);
            throw new HttpRequestException($"OBO token exchange failed: {(int)tokenResponse.StatusCode}.");
        }

        using var tokenJson = JsonDocument.Parse(tokenPayload);
        if (!tokenJson.RootElement.TryGetProperty("access_token", out var accessTokenElement))
        {
            throw new InvalidOperationException("OBO token response did not include access_token.");
        }

        var downstreamUri = new Uri(baseUri, request.Path);
        using var downstreamRequest = new HttpRequestMessage(ParseMethod(request.HttpMethod), downstreamUri);
        downstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessTokenElement.GetString());

        if (request.Headers is not null)
        {
            foreach (var (name, value) in request.Headers)
            {
                if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                downstreamRequest.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (request.Body.HasValue && AllowsBody(downstreamRequest.Method))
        {
            downstreamRequest.Content = new StringContent(request.Body.Value.GetRawText(), Encoding.UTF8, "application/json");
        }

        using var downstreamResponse = await httpClient.SendAsync(downstreamRequest, cancellationToken);
        var downstreamBody = await downstreamResponse.Content.ReadAsStringAsync(cancellationToken);
        var contentType = downstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/json";

        return new OboDownstreamResponse(downstreamResponse.StatusCode, contentType, downstreamBody);
    }

    private static HttpMethod ParseMethod(string method) => method.Trim().ToUpperInvariant() switch
    {
        "GET" => HttpMethod.Get,
        "POST" => HttpMethod.Post,
        "PUT" => HttpMethod.Put,
        "PATCH" => HttpMethod.Patch,
        "DELETE" => HttpMethod.Delete,
        _ => throw new InvalidOperationException("Unsupported HTTP method. Allowed: GET, POST, PUT, PATCH, DELETE.")
    };

    private static bool AllowsBody(HttpMethod method) =>
        method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch;
}