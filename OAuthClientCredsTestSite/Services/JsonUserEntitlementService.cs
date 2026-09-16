using System.Security.Claims;
using System.Text.Json;
using OAuthClientCredsTestSite.Models;

namespace OAuthClientCredsTestSite.Services;

public sealed class JsonUserEntitlementService(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ILogger<JsonUserEntitlementService> logger) : IUserEntitlementService
{
    private readonly object sync = new();
    private DateTime lastWriteUtc = DateTime.MinValue;
    private IReadOnlyList<UserEntitlementEntry> cachedUsers = Array.Empty<UserEntitlementEntry>();

    public bool IsAuthorized(ClaimsPrincipal user, string requiredRole, string requestPath)
    {
        EnsureLoaded();

        var oid = user.FindFirst("oid")?.Value;
        var tid = user.FindFirst("tid")?.Value;
        if (string.IsNullOrWhiteSpace(oid) || string.IsNullOrWhiteSpace(tid))
        {
            return false;
        }

        var entry = cachedUsers.FirstOrDefault(item =>
            string.Equals(item.ObjectId, oid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.TenantId, tid, StringComparison.OrdinalIgnoreCase));

        if (entry is null || !entry.Active)
        {
            return false;
        }

        var hasRole = entry.Roles.Any(role =>
            string.Equals(role, requiredRole, StringComparison.OrdinalIgnoreCase));

        if (!hasRole)
        {
            return false;
        }

        if (entry.AllowedPathPrefixes.Length == 0)
        {
            return true;
        }

        return entry.AllowedPathPrefixes.Any(prefix =>
            prefix == "*" || requestPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureLoaded()
    {
        var configuredPath = configuration["Authorization:EntitlementsFilePath"] ?? "entitlements.json";
        var fullPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);

        if (!File.Exists(fullPath))
        {
            lock (sync)
            {
                cachedUsers = Array.Empty<UserEntitlementEntry>();
                lastWriteUtc = DateTime.MinValue;
            }

            logger.LogWarning("Entitlements file not found: {Path}", fullPath);
            return;
        }

        var writeUtc = File.GetLastWriteTimeUtc(fullPath);
        if (writeUtc <= lastWriteUtc)
        {
            return;
        }

        lock (sync)
        {
            writeUtc = File.GetLastWriteTimeUtc(fullPath);
            if (writeUtc <= lastWriteUtc)
            {
                return;
            }

            var json = File.ReadAllText(fullPath);
            var fileModel = JsonSerializer.Deserialize<UserEntitlementsFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            cachedUsers = fileModel?.Users?.ToArray() ?? Array.Empty<UserEntitlementEntry>();
            lastWriteUtc = writeUtc;

            logger.LogInformation("Loaded {Count} user entitlement entries.", cachedUsers.Count);
        }
    }
}