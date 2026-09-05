using System.Net.Http.Headers;
using OAuthClientCredsTestSite.Models;

namespace OAuthClientCredsTestSite.Services;

public sealed class DownstreamApiService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public DownstreamApiService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<(string statusCode, string body)> CallApiAsync(
        string apiUrl,
        TokenResponse token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            return ("N/A", "API URL is empty.");
        }

        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            return ("N/A", "Access token is empty.");
        }

        var httpClient = _httpClientFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var statusCode = $"{(int)response.StatusCode} {response.StatusCode}";

        return (statusCode, body);
    }
}