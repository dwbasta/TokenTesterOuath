using System.Security.Claims;

namespace OAuthClientCredsTestSite.Identity;

public static class ClaimNormalizer
{
    public static bool HasAnyValue(
        ClaimsPrincipal user,
        IEnumerable<string> claimTypes,
        string requiredValue)
    {
        if (string.IsNullOrWhiteSpace(requiredValue))
        {
            return false;
        }

        foreach (var claimType in claimTypes)
        {
            var values = user.FindAll(claimType)
                .SelectMany(claim => (claim.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));

            if (values.Contains(requiredValue, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}