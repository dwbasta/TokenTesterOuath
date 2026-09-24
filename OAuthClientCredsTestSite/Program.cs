using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("jwtsettings.json", optional: true, reloadOnChange: false);

IdentityModelEventSource.ShowPII = true;

builder.Services.AddControllers();

var audiences = builder.Configuration.GetSection("Jwt:Audiences").Get<string[]>() ?? Array.Empty<string>();
var validAudiences = audiences
    .SelectMany(audience => audience.StartsWith("api://", StringComparison.OrdinalIgnoreCase)
        ? new[] { audience, audience["api://".Length..] }
        : new[] { audience, $"api://{audience}" })
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

var authority = builder.Configuration["Jwt:Authority"];
var configuredIssuers = builder.Configuration.GetSection("Jwt:ValidIssuers").Get<string[]>() ?? Array.Empty<string>();
var validIssuers = configuredIssuers
    .Where(issuer => !string.IsNullOrWhiteSpace(issuer))
    .Select(issuer => issuer.Trim())
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

var apiDisplayName = builder.Configuration["Api:DisplayName"] ?? "Token API";
var defaultReadRole = builder.Configuration["Jwt:RequiredRole"] ?? "Api.Read";
var writeRole = builder.Configuration["Jwt:RequiredWriteRole"] ?? "Api.Write";
var defaultReadScope = builder.Configuration["Jwt:RequiredScope"] ?? "api.read";
var writeScope = builder.Configuration["Jwt:RequiredWriteScope"] ?? "api.write";
var roleClaimType = builder.Configuration["Jwt:RoleClaimType"] ?? "roles";
var nameClaimType = builder.Configuration["Jwt:NameClaimType"] ?? "appid";

if (string.IsNullOrWhiteSpace(authority) || validAudiences.Length == 0)
{
    throw new InvalidOperationException(
        "JWT configuration is incomplete. Ensure jwtsettings.json has Jwt:Authority and Jwt:Audiences.");
}

if (!Uri.TryCreate(authority, UriKind.Absolute, out _))
{
    throw new InvalidOperationException("Jwt:Authority must be a valid absolute URI.");
}

if (validIssuers.Length == 0)
{
    validIssuers = new[] { authority.TrimEnd('/') };
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.RequireHttpsMetadata = true;
        options.MapInboundClaims = false;
        options.IncludeErrorDetails = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = validIssuers,
            ValidateAudience = true,
            ValidAudiences = validAudiences,
            ValidateLifetime = true,
            RoleClaimType = roleClaimType,
            NameClaimType = nameClaimType,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("JwtAuth");
                var hasAuthHeader = context.Request.Headers.ContainsKey("Authorization");
                logger.LogInformation("Authorization header present: {Present}", hasAuthHeader);
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("JwtAuth");
                logger.LogInformation("Token validated. iss={Issuer}, aud={Audience}",
                    context.Principal?.FindFirst("iss")?.Value,
                    context.Principal?.FindFirst("aud")?.Value);
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("JwtAuth");
                logger.LogError(context.Exception, "JWT authentication failed.");
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("JwtAuth");
                logger.LogWarning("JWT challenge. Error={Error}, Description={Description}",
                    context.Error, context.ErrorDescription);
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    bool HasRole(ClaimsPrincipal user, string requiredRole)
    {
        if (string.IsNullOrWhiteSpace(requiredRole))
        {
            return false;
        }

        return user.FindAll(roleClaimType)
            .Any(claim => string.Equals(claim.Value, requiredRole, StringComparison.OrdinalIgnoreCase));
    }

    static IEnumerable<string> SplitClaimValues(ClaimsPrincipal user, string claimType) =>
        user.FindAll(claimType)
            .SelectMany(claim => ExpandClaimValue(claim.Value));

    static IEnumerable<string> ExpandClaimValue(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            yield break;
        }

        var trimmed = rawValue.Trim();

        // Handle JSON array claims like: ["api.read","competitor.read"]
        if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
            TryParseJsonStringArray(trimmed, out var jsonValues))
        {
            foreach (var value in jsonValues)
            {
                yield return value;
            }

            yield break;
        }

        foreach (var part in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return part;
        }
    }

    static bool TryParseJsonStringArray(string value, out string[] values)
    {
        values = [];

        try
        {
            using var json = JsonDocument.Parse(value);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            values = json.RootElement
                .EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())
                .ToArray();

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    static bool HasScopeOrPermission(ClaimsPrincipal user, string requiredValue)
    {
        if (string.IsNullOrWhiteSpace(requiredValue))
        {
            return false;
        }

        return SplitClaimValues(user, "scope")
            .Concat(SplitClaimValues(user, "scp"))
            .Concat(SplitClaimValues(user, "permissions"))
            .Any(value => string.Equals(value, requiredValue, StringComparison.OrdinalIgnoreCase));
    }

    static bool IsClientCredentialsToken(ClaimsPrincipal user) =>
        string.Equals(user.FindFirst("gty")?.Value, "client-credentials", StringComparison.OrdinalIgnoreCase);

    static string GetPath(AuthorizationHandlerContext context)
    {
        if (context.Resource is HttpContext httpContext)
        {
            return httpContext.Request.Path.Value ?? string.Empty;
        }

        if (context.Resource is Microsoft.AspNetCore.Mvc.Filters.AuthorizationFilterContext mvcContext)
        {
            return mvcContext.HttpContext.Request.Path.Value ?? string.Empty;
        }

        return string.Empty;
    }

    var endpointRequirements = new[]
    {
        new { Prefix = "/api/data/competitors", Role = "Competitor.Reader", Permission = "competitor.read" },
        new { Prefix = "/api/data/recipes", Role = "Recipe.Reader", Permission = "recipe.read" },
        new { Prefix = "/api/data/recipies", Role = "Recipe.Reader", Permission = "recipe.read" },
        new { Prefix = "/api/data/employees", Role = "Employee.Reader", Permission = "employee.read" },
        new { Prefix = "/api/data/inventory", Role = "Inventory.Reader", Permission = "inventory.read" },
        new { Prefix = "/api/data/sales", Role = "Sales.Reader", Permission = "sales.read" },
        new { Prefix = "/api/data/customers", Role = "Customer.Reader", Permission = "customer.read" },
        new { Prefix = "/api/data/locations", Role = "Location.Reader", Permission = "location.read" },
        new { Prefix = "/api/data/reviews", Role = "Review.Reader", Permission = "review.read" },
        new { Prefix = "/api/data/inspections", Role = "Inspection.Reader", Permission = "inspection.read" },
        new { Prefix = "/api/data/employee-of-the-month", Role = "Employee.Reader", Permission = "employee.read" },
        new { Prefix = "/api/data/secret-formula/status", Role = "SecretFormula.Reader", Permission = "secretformula.read" },
        new { Prefix = "/api/data/underwater-weather", Role = "Weather.Reader", Permission = "weather.read" },
        new { Prefix = "/api/data/health/ingredients", Role = "IngredientHealth.Reader", Permission = "ingredienthealth.read" }
    };

    options.AddPolicy("ApiRead", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
        {
            var path = GetPath(context);
            var requirement = endpointRequirements.FirstOrDefault(item =>
                path.StartsWith(item.Prefix, StringComparison.OrdinalIgnoreCase));

            if (requirement is null)
            {
                return HasRole(context.User, defaultReadRole) ||
                    HasScopeOrPermission(context.User, defaultReadScope);
            }

            return HasRole(context.User, requirement.Role) ||
                HasScopeOrPermission(context.User, requirement.Permission) ||
                (IsClientCredentialsToken(context.User) && HasScopeOrPermission(context.User, defaultReadScope));
        });
    });

    options.AddPolicy("ApiWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
            HasRole(context.User, writeRole) ||
            (IsClientCredentialsToken(context.User) && HasScopeOrPermission(context.User, writeScope)));
    });
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/", () => Results.Redirect("/index.html"));
app.MapGet("/api/status", () => Results.Ok(new { name = apiDisplayName }));

app.Run();