using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OAuthClientCredsTestSite.Models;

namespace OAuthClientCredsTestSite.Services;

public sealed class ClientCredentialsTokenService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ClientCredentialsTokenService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<(TokenResponse? token, string? error)> RequestTokenAsync(ClientCredentialsRequest request, CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient();

        var tokenEndpoint = $"https://login.microsoftonline.com/{request.TenantId}/oauth2/v2.0/token";

        var form = new Dictionary<string, string>
        {
            ["client_id"] = request.ClientId,
            ["client_secret"] = request.ClientSecret,
            ["scope"] = request.Scope,
            ["grant_type"] = "client_credentials"
        };

        using var content = new FormUrlEncodedContent(form);
        using var response = await httpClient.PostAsync(tokenEndpoint, content, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return (null, $"Token endpoint error ({(int)response.StatusCode}): {payload}");
        }

        try
        {
            var token = JsonSerializer.Deserialize<TokenResponse>(payload, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                return (null, "Token response was empty or invalid.");
            }

            return (token, null);
        }
        catch (Exception ex)
        {
            return (null, $"Failed to parse token response: {ex.Message}. Raw payload: {payload}");
        }
    }
}