using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("jwtsettings.json", optional: true, reloadOnChange: false);

builder.Services.AddControllers();

var audiences = builder.Configuration.GetSection("Jwt:Audiences").Get<string[]>() ?? Array.Empty<string>();
var validAudiences = audiences
    .SelectMany(audience => audience.StartsWith("api://", StringComparison.OrdinalIgnoreCase)
        ? new[] { audience, audience["api://".Length..] }
        : new[] { audience, $"api://{audience}" })
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

var authority = builder.Configuration["Jwt:Authority"];
var apiDisplayName = builder.Configuration["Api:DisplayName"] ?? "Token API";

var defaultReadRole = builder.Configuration["Jwt:RequiredRole"] ?? "Api.Read";
var writeRole = builder.Configuration["Jwt:RequiredWriteRole"] ?? "Api.Write";

if (string.IsNullOrWhiteSpace(authority) || validAudiences.Length == 0)
{
    throw new InvalidOperationException(
        "JWT configuration is incomplete. Ensure jwtsettings.json is configured with Jwt:Authority and Jwt:Audiences.");
}

if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri))
{
    throw new InvalidOperationException("Jwt:Authority must be a valid absolute URI.");
}

var authorityPathSegments = authorityUri.AbsolutePath
    .Trim('/')
    .Split('/', StringSplitOptions.RemoveEmptyEntries);

if (authorityPathSegments.Length == 0)
{
    throw new InvalidOperationException("Jwt:Authority must include a tenant segment.");
}

var tenantSegment = authorityPathSegments[0];
var validIssuers = new[]
{
    $"https://login.microsoftonline.com/{tenantSegment}/v2.0",
    $"https://sts.windows.net/{tenantSegment}/"
};

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.RequireHttpsMetadata = true;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = validIssuers,
            ValidateAudience = true,
            ValidAudiences = validAudiences,
            ValidateLifetime = true,
            RoleClaimType = "roles",
            NameClaimType = "appid",
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("JwtAuthentication");
                logger.LogError(context.Exception, "JWT authentication failed for {Path}.", context.HttpContext.Request.Path);
                return Task.CompletedTask;
            },
            OnForbidden = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("JwtAuthorization");
                var roles = context.HttpContext.User.FindAll("roles").Select(claim => claim.Value).ToArray();
                logger.LogWarning("Authorization denied for {Path}. Roles: {Roles}.",
                    context.HttpContext.Request.Path,
                    roles.Length == 0 ? "<none>" : string.Join(", ", roles));
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    static bool HasRole(ClaimsPrincipal user, string requiredRole) =>
        user.FindAll("roles")
            .Any(claim => string.Equals(claim.Value, requiredRole, StringComparison.OrdinalIgnoreCase));

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
        new { Prefix = "/api/data/competitors", Role = "Competitor.Reader" },
        new { Prefix = "/api/data/recipes", Role = "Recipe.Reader" },
        new { Prefix = "/api/data/recipies", Role = "Recipe.Reader" },
        new { Prefix = "/api/data/employees", Role = "Employee.Reader" },
        new { Prefix = "/api/data/inventory", Role = "Inventory.Reader" },
        new { Prefix = "/api/data/sales", Role = "Sales.Reader" },
        new { Prefix = "/api/data/customers", Role = "Customer.Reader" },
        new { Prefix = "/api/data/locations", Role = "Location.Reader" },
        new { Prefix = "/api/data/reviews", Role = "Review.Reader" },
        new { Prefix = "/api/data/inspections", Role = "Inspection.Reader" },
        new { Prefix = "/api/data/employee-of-the-month", Role = "Employee.Reader" },
        new { Prefix = "/api/data/secret-formula/status", Role = "SecretFormula.Reader" },
        new { Prefix = "/api/data/underwater-weather", Role = "Weather.Reader" },
        new { Prefix = "/api/data/health/ingredients", Role = "IngredientHealth.Reader" }
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
                return HasRole(context.User, defaultReadRole);
            }

            return HasRole(context.User, requirement.Role);
        });
    });

    options.AddPolicy("ApiWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context => HasRole(context.User, writeRole));
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

app.Use(async (context, next) =>
{
    await next();

    if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    if (context.Response.HasStarted)
    {
        return;
    }

    if (context.Response.StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
    {
        context.Response.ContentType = "application/problem+json";

        var problem = new
        {
            type = "https://httpstatuses.com/" + context.Response.StatusCode,
            title = context.Response.StatusCode == StatusCodes.Status401Unauthorized ? "Unauthorized" : "Forbidden",
            status = context.Response.StatusCode,
            traceId = context.TraceIdentifier,
            detail = context.Response.StatusCode == StatusCodes.Status401Unauthorized
                ? "Token validation failed or token was not provided."
                : "Token is valid, but required role/scope for this endpoint is missing."
        };

        await context.Response.WriteAsJsonAsync(problem);
    }
});

app.MapControllers();
app.MapGet("/", () => Results.Redirect("/index.html"));
app.MapGet("/api/status", () => Results.Ok(new { name = apiDisplayName }));

app.Run();