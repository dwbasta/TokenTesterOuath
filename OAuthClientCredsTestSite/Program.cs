using Microsoft.AspNetCore.Authentication.JwtBearer;
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
var requiredRole = builder.Configuration["Jwt:RequiredRole"] ?? "Api.Read";
var apiDisplayName = builder.Configuration["Api:DisplayName"] ?? "Token API";

// Validate required JWT configuration
if (string.IsNullOrWhiteSpace(authority) || validAudiences.Length == 0)
{
    throw new InvalidOperationException(
        "JWT configuration is incomplete. Ensure jwtsettings.json is configured with Jwt:Authority and Jwt:Audiences, " +
        "or run the deployment script to populate these values at deploy time.");
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
    options.AddPolicy("ApiRead", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("roles", requiredRole);
    });

    options.AddPolicy("ApiWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("roles", "Api.Write");
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