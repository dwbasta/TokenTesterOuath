using System.Security.Claims;

namespace OAuthClientCredsTestSite.Services;

public interface IUserEntitlementService
{
    bool IsAuthorized(ClaimsPrincipal user, string requiredRole, string requestPath);
}