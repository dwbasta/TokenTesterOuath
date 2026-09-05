namespace OAuthClientCredsTestSite.Models;

public sealed class TestPageViewModel
{
    public ClientCredentialsRequest Request { get; set; } = new();

    public bool HasResult { get; set; }

    public bool IsSuccess { get; set; }

    public string? Error { get; set; }

    public TokenResponse? Token { get; set; }

    public string? ApiStatusCode { get; set; }

    public string? ApiResponseBody { get; set; }
}