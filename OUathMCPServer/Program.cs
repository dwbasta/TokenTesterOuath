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
var requiredScope = builder.Configuration["Jwt:RequiredScope"] ?? "access_as_user";

if (string.IsNullOrWhiteSpace(authority) || validAudiences.Length == 0)
{
    throw new InvalidOperationException("JWT configuration is incomplete for MCP server.");
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
            NameClaimType = "oid",
            ClockSkew = TimeSpan.FromMinutes(2)
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("McpInvoke", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
        {
            var scopes = context.User.FindAll("scp")
                .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
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

// Request tracing middleware (safe: no raw token logging)
app.Use(async (context, next) =>
{
    await next();

    var logger = context.RequestServices
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("RequestTrace");

    var user = context.User;
    var isAuthenticated = user?.Identity?.IsAuthenticated ?? false;
    var scp = user?.FindFirst("scp")?.Value ?? "<none>";
    var oid = user?.FindFirst("oid")?.Value ?? "<none>";
    var appid = user?.FindFirst("appid")?.Value ?? "<none>";
    var aud = user?.FindFirst("aud")?.Value ?? "<none>";

    logger.LogInformation(
        "MCP request {Method} {Path} => {StatusCode}. Authenticated: {Authenticated}. aud: {Audience}. scp: {Scope}. oid: {Oid}. appid: {AppId}",
        context.Request.Method,
        context.Request.Path,
        context.Response.StatusCode,
        isAuthenticated,
        aud,
        scp,
        oid,
        appid);
});

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/api/status", () => Results.Ok(new { name = "OAuth MCP Server" }));

app.Run();