namespace OAuthClientCredsTestSite.Models;

public sealed class UserEntitlementsFile
{
    public List<UserEntitlementEntry> Users { get; init; } = [];
}

public sealed class UserEntitlementEntry
{
    public string TenantId { get; init; } = string.Empty;
    public string ObjectId { get; init; } = string.Empty;
    public bool Active { get; init; } = true;
    public string[] Roles { get; init; } = [];
    public string[] AllowedPathPrefixes { get; init; } = [];
}