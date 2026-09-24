using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OUathMCPServer.Models;

namespace OUathMCPServer.Services;

public sealed class OboForwardingService(
    HttpClient httpClient,
    IConfiguration configuration,
    IHostEnvironment hostEnvironment,
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
        var downstreamScope = configuration["Obo:DownstreamScope"];
        var configuredDownstreamAudience = configuration["Obo:DownstreamAudience"];

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

        if (!Uri.TryCreate(downstreamBaseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("Obo:DownstreamApiBaseUrl must be a valid absolute URI.");
        }

        ValidateRelativePath(request.Path);

        var configuredTokenEndpoint = configuration["Obo:TokenEndpoint"];
        var configuredGrantType = configuration["Obo:GrantType"];

        var tokenEndpoint = string.IsNullOrWhiteSpace(configuredTokenEndpoint)
            ? string.Empty
            : configuredTokenEndpoint.Trim();

        if (string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            // Entra fallback when explicit OBO endpoint is not configured.
            var authorityPathSegments = authorityUri.AbsolutePath
                .Trim('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (authorityPathSegments.Length == 0)
            {
                throw new InvalidOperationException(
                    "Obo:TokenEndpoint is required for this identity provider because Jwt:Authority does not include an Entra tenant segment.");
            }

            var tenantId = authorityPathSegments[0];
            tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
        }

        if (!Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Obo:TokenEndpoint must be a valid absolute URI.");
        }

        var grantType = string.IsNullOrWhiteSpace(configuredGrantType)
            ? "urn:ietf:params:oauth:grant-type:jwt-bearer"
            : configuredGrantType.Trim();

        Dictionary<string, string> formValues;
        if (string.Equals(grantType, "urn:ietf:params:oauth:grant-type:token-exchange", StringComparison.OrdinalIgnoreCase))
        {
            var subjectTokenType = configuration["Obo:SubjectTokenType"];
            if (string.IsNullOrWhiteSpace(subjectTokenType))
            {
                subjectTokenType = "urn:ietf:params:oauth:token-type:access_token";
            }

            formValues = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = grantType,
                ["subject_token"] = userAccessToken,
                ["subject_token_type"] = subjectTokenType,
                ["scope"] = downstreamScope
            };

            if (!string.IsNullOrWhiteSpace(configuredDownstreamAudience))
            {
                formValues["audience"] = configuredDownstreamAudience;
            }
        }
        else
        {
            formValues = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["requested_token_use"] = "on_behalf_of",
                ["assertion"] = userAccessToken,
                ["scope"] = downstreamScope
            };
        }

        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(formValues)
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

        await TryWriteTokenTraceAsync(
            userAccessToken,
            accessToken,
            downstreamScope,
            cancellationToken);

        ValidateTokenCorrelation(
            userAccessToken,
            accessToken,
            downstreamScope,
            configuredDownstreamAudience);

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

        if (downstreamResponse.StatusCode >= System.Net.HttpStatusCode.BadRequest &&
            string.IsNullOrWhiteSpace(responseBody))
        {
            responseBody = JsonSerializer.Serialize(new
            {
                error = "downstream_request_failed",
                statusCode = (int)downstreamResponse.StatusCode,
                method = method.Method,
                path = request.Path,
                detail = "Downstream API returned an error with an empty response body."
            });
            contentType = "application/json";
        }

        return new OboForwardResult((int)downstreamResponse.StatusCode, contentType, responseBody);
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Path is required.");
        }

        if (Uri.TryCreate(path, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Path must be relative.");
        }

        if (!path.StartsWith('/'))
        {
            throw new InvalidOperationException("Path must start with '/'.");
        }

        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Path cannot start with '//'.");
        }

        if (path.Contains('\\'))
        {
            throw new InvalidOperationException("Path cannot contain backslashes.");
        }
    }

    private static void ValidateTokenCorrelation(
        string incomingUserToken,
        string downstreamAccessToken,
        string downstreamScope,
        string? configuredDownstreamAudience)
    {
        var incomingClaims = ParseJwtPayload(incomingUserToken);
        var downstreamClaims = ParseJwtPayload(downstreamAccessToken);

        var incomingTid = TryGetClaim(incomingClaims, "tid");
        var downstreamTid = TryGetClaim(downstreamClaims, "tid");
        if (!string.IsNullOrWhiteSpace(incomingTid) &&
            !string.IsNullOrWhiteSpace(downstreamTid) &&
            !string.Equals(incomingTid, downstreamTid, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Tenant correlation failed between incoming token and downstream OBO token.");
        }

        var incomingOid = TryGetClaim(incomingClaims, "oid");
        var downstreamOid = TryGetClaim(downstreamClaims, "oid");
        if (!string.IsNullOrWhiteSpace(incomingOid) &&
            !string.IsNullOrWhiteSpace(downstreamOid) &&
            !string.Equals(incomingOid, downstreamOid, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("User correlation failed between incoming token and downstream OBO token.");
        }

        var expectedAudience = ResolveExpectedDownstreamAudience(downstreamScope, configuredDownstreamAudience);
        var actualAudience = TryGetClaim(downstreamClaims, "aud");
        if (!string.IsNullOrWhiteSpace(expectedAudience) &&
            !string.IsNullOrWhiteSpace(actualAudience) &&
            !string.Equals(expectedAudience, actualAudience, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Downstream OBO token audience does not match configured target resource.");
        }

        var expectedScopeName = ResolveExpectedScopeName(downstreamScope);
        var actualScopes = TryGetClaim(downstreamClaims, "scp");
        if (!string.IsNullOrWhiteSpace(expectedScopeName) &&
            !string.Equals(expectedScopeName, ".default", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(actualScopes))
        {
            var scopeList = actualScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!scopeList.Contains(expectedScopeName, StringComparer.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Downstream OBO token does not contain expected delegated scope.");
            }
        }
    }

    private static string ResolveExpectedDownstreamAudience(string scope, string? configuredAudience)
    {
        if (!string.IsNullOrWhiteSpace(configuredAudience))
        {
            return configuredAudience;
        }

        var separatorIndex = scope.LastIndexOf('/');
        if (separatorIndex <= 0)
        {
            return scope;
        }

        return scope[..separatorIndex];
    }

    private static string ResolveExpectedScopeName(string scope)
    {
        var separatorIndex = scope.LastIndexOf('/');
        if (separatorIndex < 0 || separatorIndex == scope.Length - 1)
        {
            return string.Empty;
        }

        return scope[(separatorIndex + 1)..];
    }

    private static Dictionary<string, string> ParseJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            throw new InvalidOperationException("Token format is invalid.");
        }

        var payloadBytes = Base64UrlDecode(parts[1]);
        using var payloadJson = JsonDocument.Parse(payloadBytes);

        var claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in payloadJson.RootElement.EnumerateObject())
        {
            claims[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                _ => property.Value.GetRawText()
            };
        }

        return claims;
    }

    private static string TryGetClaim(Dictionary<string, string> claims, string claimType) =>
        claims.TryGetValue(claimType, out var value) ? value : string.Empty;

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        while (padded.Length % 4 != 0)
        {
            padded += "=";
        }

        return Convert.FromBase64String(padded);
    }

    private static HttpMethod ParseMethod(string method) =>
        method.Trim().ToUpperInvariant() switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "PATCH" => HttpMethod.Patch,
            "DELETE" => HttpMethod.Delete,
            _ => throw new InvalidOperationException($"Unsupported method: {method}")
        };

    private static bool AllowsBody(HttpMethod method) =>
        method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch;

    private async Task TryWriteTokenTraceAsync(
        string incomingUserToken,
        string downstreamAccessToken,
        string downstreamScope,
        CancellationToken cancellationToken)
    {
        var writeTokenTraceFile = configuration["Obo:WriteTokenTraceFile"];
        if (!bool.TryParse(writeTokenTraceFile, out var shouldWrite) || !shouldWrite)
        {
            return;
        }

        var tokenTraceFilePath = configuration["Obo:TokenTraceFilePath"];
        if (string.IsNullOrWhiteSpace(tokenTraceFilePath))
        {
            return;
        }

        var traceFilePath = Path.Combine(hostEnvironment.ContentRootPath, tokenTraceFilePath);
        var traceDirectory = Path.GetDirectoryName(traceFilePath);
        if (!string.IsNullOrWhiteSpace(traceDirectory))
        {
            Directory.CreateDirectory(traceDirectory);
        }

        var incomingClaims = ParseJwtPayload(incomingUserToken);
        var downstreamClaims = ParseJwtPayload(downstreamAccessToken);

        var traceEntry = new
        {
            TimestampUtc = DateTime.UtcNow,
            DownstreamScope = downstreamScope,
            Incoming = new
            {
                Token = incomingUserToken,
                Audience = TryGetClaim(incomingClaims, "aud"),
                Issuer = TryGetClaim(incomingClaims, "iss"),
                Subject = TryGetClaim(incomingClaims, "sub"),
                ObjectId = TryGetClaim(incomingClaims, "oid"),
                TenantId = TryGetClaim(incomingClaims, "tid"),
                Scope = TryGetClaim(incomingClaims, "scp"),
                ScopeAlt = TryGetClaim(incomingClaims, "scope"),
                Roles = TryGetClaim(incomingClaims, "roles"),
                Permissions = TryGetClaim(incomingClaims, "permissions"),
                NamespacedRoles = TryGetClaim(incomingClaims, "https://oktaapp.resurgemus.xyz/roles")
            },
            Downstream = new
            {
                Token = downstreamAccessToken,
                Audience = TryGetClaim(downstreamClaims, "aud"),
                Issuer = TryGetClaim(downstreamClaims, "iss"),
                Subject = TryGetClaim(downstreamClaims, "sub"),
                ObjectId = TryGetClaim(downstreamClaims, "oid"),
                TenantId = TryGetClaim(downstreamClaims, "tid"),
                Scope = TryGetClaim(downstreamClaims, "scp"),
                ScopeAlt = TryGetClaim(downstreamClaims, "scope"),
                Roles = TryGetClaim(downstreamClaims, "roles"),
                Permissions = TryGetClaim(downstreamClaims, "permissions"),
                NamespacedRoles = TryGetClaim(downstreamClaims, "https://oktaapp.resurgemus.xyz/roles")
            }
        };

        var traceLine = JsonSerializer.Serialize(traceEntry) + Environment.NewLine;
        await File.AppendAllTextAsync(traceFilePath, traceLine, cancellationToken);
    }
}

