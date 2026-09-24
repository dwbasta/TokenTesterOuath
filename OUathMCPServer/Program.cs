using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OUathMCPServer.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("jwtsettings.json", optional: true, reloadOnChange: false);
builder.Configuration.AddJsonFile("obosettings.json", optional: true, reloadOnChange: false);

builder.Services.AddControllers();
builder.Services.AddHttpClient<OboForwardingService>();

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

var requiredRole = builder.Configuration["Jwt:RequiredRole"] ?? "Mcp.Invoke";
var requiredScope = builder.Configuration["Jwt:RequiredScope"] ?? "access_as_user";
var roleClaimType = builder.Configuration["Jwt:RoleClaimType"] ?? "roles";
var nameClaimType = builder.Configuration["Jwt:NameClaimType"] ?? "oid";

if (string.IsNullOrWhiteSpace(authority) || validAudiences.Length == 0)
{
    throw new InvalidOperationException("JWT configuration is incomplete for MCP server.");
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
            OnAuthenticationFailed = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("McpJwtAuth");
                logger.LogError(context.Exception, "MCP JWT authentication failed.");
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("McpJwtAuth");
                logger.LogInformation("MCP token validated. iss={Issuer}, aud={Audience}, scope={Scope}",
                    context.Principal?.FindFirst("iss")?.Value,
                    context.Principal?.FindFirst("aud")?.Value,
                    context.Principal?.FindFirst("scope")?.Value);
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("McpJwtAuth");
                logger.LogWarning("MCP JWT challenge. Error={Error}, Description={Description}",
                    context.Error, context.ErrorDescription);
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("McpInvoke", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
        {
            var hasRole = context.User.FindAll(roleClaimType)
                .Any(claim => string.Equals(claim.Value, requiredRole, StringComparison.OrdinalIgnoreCase));

            if (hasRole)
            {
                return true;
            }

            var scopes = context.User.FindAll("scp")
                .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Concat(
                    context.User.FindAll("scope")
                        .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
                .Concat(context.User.FindAll("permissions").Select(claim => claim.Value));

            return scopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase);
        });
    });
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

// TEMPORARY DEBUG MIDDLEWARE - logs incoming bearer token before auth runs.
// Security warning: logs raw bearer tokens. Remove after troubleshooting.
var writeIncomingTokenTrace = builder.Configuration["Obo:WriteTokenTraceFile"];
if (bool.TryParse(writeIncomingTokenTrace, out var shouldTraceIncoming) && shouldTraceIncoming)
{
    var incomingTraceRelativePath = builder.Configuration["Obo:TokenTraceFilePath"] ?? "logs/obo-token-trace.jsonl";
    var incomingTraceFilePath = Path.Combine(app.Environment.ContentRootPath, incomingTraceRelativePath.Replace("obo-token-trace", "incoming-request-trace"));
    var incomingTraceDirectory = Path.GetDirectoryName(incomingTraceFilePath);
    if (!string.IsNullOrWhiteSpace(incomingTraceDirectory))
    {
        Directory.CreateDirectory(incomingTraceDirectory);
    }

    app.Use(async (context, next) =>
    {
        var hasAuthHeader = context.Request.Headers.TryGetValue("Authorization", out var authHeaderValue);
        var entry = new
        {
            TimestampUtc = DateTime.UtcNow,
            Path = context.Request.Path.Value,
            Method = context.Request.Method,
            HasAuthorizationHeader = hasAuthHeader,
            RawAuthorizationHeader = hasAuthHeader ? authHeaderValue.ToString() : null
        };

        var line = System.Text.Json.JsonSerializer.Serialize(entry) + Environment.NewLine;
        await File.AppendAllTextAsync(incomingTraceFilePath, line);

        await next();
    });
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/api/status", () => Results.Ok(new { name = "OAuth MCP Server" }));

app.Run();