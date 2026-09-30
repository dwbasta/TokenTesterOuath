# Deploying to IIS

These instructions assume you have already received the published website files, preferably as:

OAuthClientCredsTestSite.zip

Run all commands below on the IIS server in **PowerShell opened as Administrator**.

## 1. Copy the files to the server

Copy these files to the server, for example into `C:\Deploy`:

C:\Deploy\OAuthClientCredsTestSite.zip  
C:\Deploy\deploy-iis-vm.ps1  
C:\Deploy\request-letsencrypt-certificate.ps1  

The deployment script installs/configures IIS, installs the .NET 8 Hosting Bundle only when the IIS ASP.NET Core Module is missing, creates the application pool and website, and copies the published files.

## 2. Run the deployment script

Update the hostname and run:

C:\Deploy\deploy-iis-vm.ps1 ` 
  -SourcePath "C:\Deploy\OAuthClientCredsTestSite.zip" ` 
  -SiteName "OAuthClientCredsTestSite" ` 
  -AppPoolName "OAuthClientCredsTestSitePool" ` 
  -PhysicalPath "C:\inetpub\wwwroot\OAuthClientCredsTestSite" ` 
  -Port 80 ` 
  -HostName "your-hostname.example.com"

If the site is only being tested locally, omit `-HostName`:

C:\Deploy\deploy-iis-vm.ps1 ` 
  -SourcePath "C:\Deploy\OAuthClientCredsTestSite.zip" ` 
  -PhysicalPath "C:\inetpub\wwwroot\OAuthClientCredsTestSite" ` 
  -Port 80

When updating an existing site, it adds the requested HTTP binding only if it is missing and preserves all existing HTTPS bindings and certificate configuration. To redeploy without adding or changing any HTTP binding, use:

C:\Deploy\deploy-iis-vm.ps1 ` 
  -SourcePath "C:\Deploy\OAuthClientCredsTestSite.zip" ` 
  -SkipHttpBinding

`-SkipHttpBinding` is for an existing IIS site. A new site needs an initial binding. The script automatically skips the .NET 8 Hosting Bundle download when `IIS AspNetCore Module V2\aspnetcorev2.dll` is already installed.

## 3. Configure the API during deployment

When `jwtsettings.json` is not already configured, the deployment script prompts for:

- The Entra tenant ID
- The **Protected API app registration's Client ID** (not the calling client app's Client ID)

Example prompts:

Enter the Entra tenant ID: <tenant-guid>  
Enter the Protected API app registration Client ID: <protected-api-client-guid>

The script writes the values to:

C:\inetpub\wwwroot\OAuthClientCredsTestSite\jwtsettings.json

On later deployments, the existing JWT settings are preserved unless new `-TenantId` or `-ProtectedApiClientId` values are supplied. The API does not expose a runtime setup page and does not require the IIS app pool to write configuration files.

## 4. Prepare for HTTPS

The application redirects HTTP requests to HTTPS in production. Before requesting the certificate:

- Point the DNS name to the IIS server.
- Allow inbound TCP ports `80` and `443` through the server firewall.
- Confirm that the site has an HTTP binding for the DNS name.

The Let’s Encrypt script below requests the certificate and creates the HTTPS IIS binding automatically.

## 5. Create the Let's Encrypt certificate

The repository includes `scripts\request-letsencrypt-certificate.ps1`. This script uses win-acme to request a certificate, install it in IIS, and create the HTTPS binding.

Before running it:

- Install win-acme on the server at `C:\Tools\win-acme`, or provide a different path with `-WacsExe`.
- Make sure the DNS name points to the IIS server.
- Make sure inbound TCP port `80` is allowed through the firewall. Let's Encrypt uses the HTTP binding to validate domain ownership.
- Run PowerShell as Administrator.

Run the script using the deployed site and DNS name:

C:\Deploy\request-letsencrypt-certificate.ps1 ` 
  -SiteName "OAuthClientCredsTestSite" ` 
  -DnsName "example.contoso.xyz" ` 
  -EmailAddress "admin@example.com" ` 
  -WacsExe "C:\Tools\win-acme\wacs.exe"

The script first verifies that the IIS site exists and adds the HTTP host-header binding if needed. win-acme then requests and installs the certificate. After it completes, confirm that IIS has an HTTPS binding for the site:

Import-Module WebAdministration  
Get-WebBinding -Name "OAuthClientCredsTestSite"

Open the site using HTTPS:

https://<protected-api-hostname>/ 

## 6. Call the protected API

This deployment exposes a public status page at `/`, but it does not request or store client credentials. The API endpoints remain protected.

The repository includes `scripts\call-protected-api.ps1` for a direct test call. Edit the configuration values at the top of that script with the calling client app's tenant ID, client ID, client secret, protected API scope, and API URL. Do not commit a real client secret.

Run it with:

C:\Deploy\call-protected-api.ps1

Your client application must:

1. Request a client-credentials token from Microsoft Entra ID using the protected API scope:

   api://YOUR_PROTECTED_API_CLIENT_ID/.default

2. Have the protected API's `Api.Read` application permission assigned and consented.
3. Call the API with the token:

   GET <protected-api-base-url>/api/data  
   Authorization: Bearer YOUR_ACCESS_TOKEN

A valid token returns HTTP `200 OK` with a joke secret-recipe payload:

{
  "message": "Read allowed.",
  "source": "Krusty Krab secret recipe (definitely official)",
  "secretRecipe": {
    "ingredients": [
      "1 perfectly ordinary burger patty",
      "A pinch of nautical nonsense",
      "Two slices of confidence",
      "One classified mystery ingredient"
    ],
    "instructions": "Assemble with care, keep the formula secret, and serve with a wink."
  }
}

A missing or invalid token returns `401 Unauthorized`. A valid token without the required `Api.Read` role returns `403 Forbidden`.

## API endpoints

All `GET /api/data/*` endpoints require the `Api.Read` application role. The `POST /api/data` endpoint requires `Api.Write`.

| Method | Endpoint | Purpose |
| --- | --- | --- |
| GET | `/api/data` | Employee directory and demo recipe payload |
| GET | `/api/data/employees` | Employee directory |
| GET | `/api/data/competitors` | Competitor directory |
| GET | `/api/data/recipes` | Recipe archive |
| GET | `/api/data/recipies` | Backward-compatible spelling alias for recipes |
| GET | `/api/data/inventory` | Inventory quantities and reorder status |
| GET | `/api/data/sales/daily` | Daily sales summary |
| GET | `/api/data/customers` | Fictional customer loyalty data |
| GET | `/api/data/locations` | Restaurant locations and operating status |
| GET | `/api/data/reviews` | Fictional customer reviews and ratings |
| GET | `/api/data/inspections` | Food-safety inspection results |
| GET | `/api/data/employee-of-the-month` | Current employee award details |
| GET | `/api/data/secret-formula/status` | Classified formula status without recipe details |
| GET | `/api/data/underwater-weather` | Fictional underwater conditions |
| GET | `/api/data/health/ingredients` | Ingredient freshness and temperature checks |
| POST | `/api/data` | Protected write-operation test endpoint |

The splash page lists the protected endpoints without making authenticated requests. Set `Api:DisplayName` in `appsettings.json` to customize the name shown on the splash page:

{
  "Api": {
    "DisplayName": "My Protected Token API"
  }
}

The endpoint data is fictional demonstration data. Do not use the sample salary, address, phone, or customer information as real records.

## 7. Distribution package for consumers

This repository supports distributing pre-published application artifacts instead of requiring consumers to build from source.

Provide both published folders from:

C:\temp\publish\OAuthClientCredsTestSite  
C:\temp\publish\OUathMCPServer  

Recommended package layout:

TokenTesterDeployment\
  OAuthClientCredsTestSite\   # published web app output
  OUathMCPServer\             # published MCP server output
  deploy-iis-vm.ps1
  deploy-mcp-iis-vm.ps1
  request-letsencrypt-certificate.ps1

Consumers can deploy the published outputs directly to IIS without running `dotnet build`.

## 8. Placeholder conventions

Use placeholders in all setup instructions so consumers can map values to their own environment:

- `<mcp-server-base-url>`: MCP host base URL, for example `https://mcp.example.com`
- `<protected-api-base-url>`: protected API host base URL, for example `https://api.example.com`
- `<tenant-id>`: Microsoft Entra tenant GUID
- `<mcp-api-app-id>`: MCP server API app registration GUID
- `<protected-api-app-id>`: downstream protected API app registration GUID
- `<copilot-client-app-id>`: Copilot connector client app registration GUID

### 9. Copilot client setup (OAuth)

Use manual OAuth configuration for the MCP server integration.

- Server URL: `<mcp-server-base-url>/mcp`
- OAuth mode: Manual
- Authorization URL: `https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/authorize`
- Token URL: `https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token`
- Refresh Token URL: `https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token`
- Scope: `api://<mcp-api-app-id>/access_as_user openid profile offline_access`

Client app requirements:

1. Use the Copilot connector client app registration client ID.
2. Use a valid client secret **value** from that same app registration.
3. Add the exact redirect URI used by Copilot/APIM (for example `https://global.consent.azure-apim.net/redirect/...`).
4. Grant delegated permission to MCP scope `access_as_user`.
5. Grant admin consent.

## 10. MCP connector setup

For custom connector or MCP client testing, ensure the MCP host exposes:

- `GET /.well-known/mcp`
- `POST /mcp`
- `GET /.well-known/oauth-protected-resource`
- `GET /.well-known/oauth-protected-resource/mcp`
- `GET /.well-known/oauth-authorization-server`
- `GET /.well-known/oauth-authorization-server/mcp`
- `GET /.well-known/openid-configuration`
- `GET /.well-known/openid-configuration/mcp`

### Validation checks

Invoke-RestMethod <mcp-server-base-url>/.well-known/mcp  
Invoke-RestMethod -Method Post <mcp-server-base-url>/mcp -ContentType "application/json" -Body '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}'  
Invoke-RestMethod -Method Post <mcp-server-base-url>/mcp -ContentType "application/json" -Body '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'

## 11. Protected API role configuration

The protected API authorizes by endpoint role/scope mapping. Ensure users/groups are assigned the required app roles in the protected API enterprise app.

Current role set used by this project:

- `Api.Read`
- `Api.Write`
- `Competitor.Reader`
- `Recipe.Reader`
- `Employee.Reader`
- `Inventory.Reader`
- `Sales.Reader`
- `Customer.Reader`
- `Location.Reader`
- `Review.Reader`
- `Inspection.Reader`
- `SecretFormula.Reader`
- `Weather.Reader`
- `IngredientHealth.Reader`

Recommended assignment model:

- Assign users/groups only the minimum required roles.
- Use endpoint-specific roles (for example only `Recipe.Reader`) unless broad access is intended.

### Screenshots for this section

![MCP server API permissions showing delegated downstream API permission setup](docs/images/mcpserver-api-permissions-delegated.png)

Use this screen to verify the MCP server app has delegated permissions to the protected API and that admin consent is granted.

### MCP app registration checks

In the MCP server app registration:

- `Expose an API`:
  - Application ID URI is set (example: `api://<mcp-api-app-id>`)
  - Scope exists: `access_as_user`
- `Authorized client applications`:
  - Add `<copilot-client-app-id>`
  - Authorize scope `access_as_user`

![MCP server Expose an API with authorized client application for access_as_user](docs/images/mcpserver-expose-api-authorized-client.png)

### Role catalog reference

![Protected API app roles catalog](docs/images/protected-api-app-roles.png)

Use this screen when mapping endpoint access to protected API roles (for example `Api.Read`, `Api.Write`, `Competitor.Reader`)

## 12. MCP server scope configuration

MCP server access policy expects delegated scope in incoming access tokens.

Configure `OUathMCPServer\jwtsettings.json`:
 
{
  "Jwt": {
    "Authority": "https://login.microsoftonline.com/<tenant-id>/v2.0",
    "Audiences": [
      "api://<mcp-api-app-id>"
    ],
    "RequiredScope": "access_as_user"
  }
}

## 13. MCP OBO configuration (`OUathMCPServer/obosettings.json`)

{
  "Obo": {
    "ClientId": "<mcp-server-client-app-id>",
    "ClientSecret": "<mcp-server-client-app-secret>",
    "DownstreamApiBaseUrl": "<protected-api-base-url>",
    "DownstreamScope": "api://<protected-api-app-id>/Competitor.Read",
    "DownstreamAudience": "api://<protected-api-app-id>"
  }
}

## 14. Reset or remove deployment settings

If deployment settings need to be reset, remove generated runtime configuration files and redeploy with fresh values.

Typical reset actions:

Remove-Item "C:\inetpub\wwwroot\OAuthClientCredsTestSite\jwtsettings.json" -Force -ErrorAction SilentlyContinue  
Remove-Item "C:\inetpub\wwwroot\OUathMCPServer\jwtsettings.json" -Force -ErrorAction SilentlyContinue  
Remove-Item "C:\inetpub\wwwroot\OUathMCPServer\obosettings.json" -Force -ErrorAction SilentlyContinue  

Then redeploy and reapply configuration values.

Optional cleanup for local publish output:

Remove-Item "C:\temp\publish\OAuthClientCredsTestSite" -Recurse -Force -ErrorAction SilentlyContinue  
Remove-Item "C:\temp\publish\OUathMCPServer" -Recurse -Force -ErrorAction SilentlyContinue  

## 15. Exercise 1: Client credentials flow example (app-only)

Use this flow for daemon/service-to-service calls to the protected API.

### Token request
 
$tenantId = "<tenant-id>"
$clientId = "<calling-client-app-id>"
$clientSecret = "<calling-client-app-secret>"
$scope = "api://<protected-api-app-id>/.default"

$token = Invoke-RestMethod -Method Post `
  -Uri "https://login.microsoftonline.com/$tenantId/oauth2/v2.0/token" `
  -ContentType "application/x-www-form-urlencoded" `
  -Body @{ 
    client_id     = $clientId 
    client_secret = $clientSecret 
    scope         = $scope 
    grant_type    = "client_credentials" 
  }

$token.access_token

### API call
 
$apiUrl = "<protected-api-base-url>/api/data"
Invoke-RestMethod -Method Get -Uri $apiUrl -Headers @{ Authorization = "Bearer $($token.access_token)" }

Required permission assignment: the calling client app must be assigned protected API **application roles** (for example `Api.Read`) and admin consent must be granted.

### Flow Diagrams

To better understand the interactions between components, refer to the following flow diagrams:

#### Client Credentials Flow

```mermaid
sequenceDiagram
    autonumber
    participant App as "Calling Client App"
    participant Entra as "Microsoft Entra ID"
    participant API as "Protected API"

    App->>Entra: "POST /token grant_type=client_credentials scope=api://<protected-api-app-id>/.default"
    Entra-->>App: "Access token (app-only)"
    App->>API: "GET /api/data with Bearer token"
    API->>API: "Validate iss/aud + app role (e.g., Api.Read)"
    API-->>App: "200 OK or 403 Forbidden"
```

## 16. Exercise 2: Delegated MCP flow example (Copilot -> MCP -> API via OBO)

Use this flow when user context must be preserved.

1. Copilot signs in the user and acquires token for MCP audience `api://<mcp-api-app-id>` with scope `access_as_user`.
2. Copilot calls MCP endpoint (`/mcp`) with that bearer token.
3. MCP validates `aud` and `scp=access_as_user`.
4. MCP performs On-Behalf-Of (OBO) to obtain downstream delegated token for protected API scopes.
5. MCP calls protected API with downstream token.

In `OUathMCPServer\obosettings.json`:
 
{
  "Obo": {
    "ClientId": "<mcp-server-client-app-id>",
    "ClientSecret": "<mcp-server-client-app-secret>",
    "DownstreamApiBaseUrl": "<protected-api-base-url>",
    "DownstreamScope": "api://<protected-api-app-id>/<delegated-scope>",
    "DownstreamAudience": "api://<protected-api-app-id>"
  }
}

### OBO token validation and entitlement checks

For Exercise 2, enforce validation at **both** hops:

1. **MCP inbound token validation**
   - Validate issuer (`iss`) and audience (`aud=api://<mcp-api-app-id>`).
   - Require delegated scope `scp=access_as_user`.
   - Reject invalid/missing token with `401 Unauthorized`.

2. **Protected API entitlement validation**
   - Validate downstream OBO token issuer and audience (`aud=api://<protected-api-app-id>`).
   - Enforce required delegated scope or role for the target endpoint.
   - If token is valid but missing entitlement, return `403 Forbidden`.

Recommended outcome mapping:

- `401 Unauthorized`: token missing, invalid, expired, wrong issuer, or wrong audience.
- `403 Forbidden`: token valid, but user/app lacks required permission for endpoint.

### Flow Diagram

```mermaid
sequenceDiagram
    autonumber
    participant User as User in Copilot
    participant Copilot as Copilot Client/Agent
    participant Entra as Microsoft Entra ID
    participant MCP as MCP Server
    participant API as Protected API

    User->>Copilot: Sign in
    Copilot->>Entra: Request delegated token for api://<mcp-api-app-id>/access_as_user
    Entra-->>Copilot: Access token (delegated)
    Copilot->>MCP: POST /mcp with Bearer token
    MCP->>MCP: Validate aud + scp=access_as_user
    MCP->>Entra: OBO token request for api://<protected-api-app-id>/<delegated-scope>
    Entra-->>MCP: Downstream delegated token
    MCP->>API: Call protected endpoint with downstream token
    API-->>MCP: Protected data response
    MCP-->>Copilot: MCP tool/result response

```

## 17. Test-only access token trace logging (MCP)

For troubleshooting OAuth/OBO flows, the MCP server can write both the inbound token (client -> MCP) and downstream OBO token (MCP -> API) to a JSONL trace file.

> Security warning: this logs raw bearer tokens. Enable only in test environments and disable immediately after troubleshooting.

### `OUathMCPServer/obosettings.json`

{
  "Obo": {
    "WriteTokenTraceFile": true,
    "TokenTraceFilePath": "logs/obo-token-trace.jsonl"
  }
}
- `WriteTokenTraceFile`: enables token trace output.
- `TokenTraceFilePath`: relative path under app root, or absolute path.

### IIS permission requirement

The IIS app pool identity must have write access to the trace folder.

```powershell
Import-Module WebAdministration
$sitePath = "C:\inetpub\wwwroot\OUathMCPServer"
$appPool = "OUathMCPServerPool"

New-Item -ItemType Directory -Path "$sitePath\logs" -Force | Out-Null
icacls "$sitePath\logs" /grant "IIS AppPool\${appPool}:(OI)(CI)(M)" /T
Restart-WebAppPool -Name $appPool
```

### Verify trace output

After issuing an MCP tool call, check:

```powershell
Get-Content "C:\inetpub\wwwroot\OUathMCPServer\logs\obo-token-trace.jsonl" -Tail 1
```

Each line contains:
- `incoming.token` + decoded claims (token received by MCP)
- `downstream.token` + decoded claims (OBO token sent to downstream API)

Disable tracing when done:

{
  "Obo": {
    "WriteTokenTraceFile": false
  }
}

## Troubleshooting: Let's Encrypt `403 unauthorized` during certificate request

If `scripts\request-letsencrypt-certificate.ps1` is run without parameters, it uses placeholder defaults:

- `SiteName = OAuthClientCredsTestSite`
- `DnsName = example.contoso.xyz`

That can create or reuse an invalid host-header binding and cause ACME validation to fail with errors like:

- `Authorization result: invalid`
- `acme:error:unauthorized`
- `Invalid response ... /.well-known/acme-challenge/...: 403`

### Fix

1. Check current bindings:

Import-Module WebAdministration
Get-WebBinding -Name "OAuthClientCredsTestSite" | Select-Object Protocol, BindingInformation

2. Remove placeholder binding if present:

Remove-WebBinding -Name "OAuthClientCredsTestSite" -Protocol http -Port 80 -IPAddress "*" -HostHeader "example.contoso.xyz"

3. Re-run with explicit values:

.\request-letsencrypt-certificate.ps1 ` 
  -SiteName "OAuthClientCredsTestSite" ` 
  -DnsName "<your-real-dns-name>" ` 
  -EmailAddress "<your-email>" ` 
  -WacsExe "C:\Tools\win-acme\wacs.exe"

### Required environment checks

- `<your-real-dns-name>` resolves to this IIS server's public IP.
- Inbound TCP port `80` is allowed.
- No upstream proxy/WAF/CDN is blocking `/.well-known/acme-challenge/*`.

## Copilot with Auth0/Okta protected MCP and downstream API

This section documents end-to-end setup for:

- Copilot -> connector/APIM (OAuth)
- Connector/APIM -> MCP server protected by Auth0/Okta
- MCP -> downstream protected app/API protected by Auth0/Okta (via OBO/token-exchange)

### 1. Prerequisites

Ensure these hosts are deployed and reachable over HTTPS:

- MCP host: `https://oktatokenmcp.resurgemus.xyz`
- Protected app/API host: `https://oktatokenapp.resurgemus.xyz` (audience identifier example: `OKTAAPP.Resurgemus.xyz`)

Ensure MCP health endpoints respond:

- `GET https://oktatokenmcp.resurgemus.xyz/api/status`
- `GET https://oktatokenmcp.resurgemus.xyz/.well-known/mcp`

### 2. Auth0 objects to create/verify

1. **MCP API** (Auth0 → Applications → APIs)
   - Identifier (audience): `https://oktatokenmcp.resurgemus.xyz`
   - Permissions defined: `access_as_user`
   - RBAC Settings: Enable RBAC = ON, Add Permissions in the Access Token = ON

2. **Protected/downstream API** (Auth0 → Applications → APIs)
   - Identifier (audience): `OKTAAPP.Resurgemus.xyz`
   - Permissions defined: `api.read`, `api.write`, plus any endpoint-specific permissions (for example `competitor.read`)
   - RBAC Settings: Enable RBAC = ON, Add Permissions in the Access Token = ON

3. **Copilot/connector-facing application** (Auth0 → Applications → Applications)
   - Type: Regular Web Application
   - Allowed Callback URLs: include every redirect URI used by every client that signs in through this app (one per line), for example:
     - `https://oauth.usebruno.com/callback`
     - `https://global.consent.azure-apim.net/redirect/<your-connector-redirect-id>`
   - API Access tab → grant User-delegated Access to the MCP API's `access_as_user` permission
   - Advanced Settings → Grant Types: confirm `Authorization Code` and `Refresh Token` are enabled

4. **MCP confidential app** (used for downstream OBO/token exchange)
   - Client ID/Secret used in `OUathMCPServer/obosettings.json`
   - API Access tab → grant User-delegated Access to the protected API's permissions (`api.read`, plus any endpoint-specific permissions your policies check)

5. **Roles** (Auth0 → User Management → Roles) — optional, only if you enforce role-based checks instead of/in addition to permission checks
   - Create roles matching required role claims (for example `Api.Read`, `Competitor.Reader`)
   - Map each role to the corresponding API permission(s)
   - Assign roles to test users

6. **Users** (Auth0 → User Management → Users)
   - Assign roles and/or ensure the user's delegated grant includes the permissions needed for the endpoints being tested

> Auth0 delegated tokens commonly emit granted permissions in a `permissions` claim as a JSON array string (for example `["api.read","competitor.read"]`), and may not emit a `roles` claim unless you add an Auth0 Action to inject one. Prefer permission-based authorization checks over role-only checks when using Auth0.

### 3. MCP runtime configuration

`C:\inetpub\wwwroot\OUathMCPServer\jwtsettings.json`:

​```json
{
  "Jwt": {
    "Provider": "Okta",
    "Authority": "https://dev-resurgemus.us.auth0.com",
    "ValidIssuers": [
      "https://dev-resurgemus.us.auth0.com",
      "https://dev-resurgemus.us.auth0.com/"
    ],
    "Audiences": [
      "https://oktatokenmcp.resurgemus.xyz"
    ],
    "RequiredRole": "Mcp.Invoke",
    "RequiredScope": "access_as_user",
    "RoleClaimType": "roles",
    "NameClaimType": "sub"
  }
}
​```

`C:\inetpub\wwwroot\OUathMCPServer\obosettings.json`:

​```json
{
  "Obo": {
    "ClientId": "<mcp-confidential-client-id>",
    "ClientSecret": "<mcp-confidential-client-secret>",
    "DownstreamApiBaseUrl": "https://oktatokenapp.resurgemus.xyz/",
    "DownstreamScope": "access_as_user",
    "DownstreamAudience": "OKTAAPP.Resurgemus.xyz",
    "TokenEndpoint": "https://dev-resurgemus.us.auth0.com/oauth/token",
    "GrantType": "urn:ietf:params:oauth:grant-type:jwt-bearer",
    "WriteTokenTraceFile": false,
    "TokenTraceFilePath": "logs/obo-token-trace.jsonl"
  }
}
​```

> `GrantType` and `TokenEndpoint` are required here for Auth0. If omitted, the app falls back to an Entra-style token endpoint, which does not exist for Auth0 tenants. On every redeploy, pass `-OboGrantType` and `-OboTokenEndpoint` explicitly to `deploy-mcp-iis-vm.ps1` so these values are not silently dropped or reset.
>
> Auth0's `urn:ietf:params:oauth:grant-type:token-exchange` grant type requires additional tenant-level configuration and a supported `subject_token_type`; using an unsupported value returns `Invalid subject_token_type`. Prefer `urn:ietf:params:oauth:grant-type:jwt-bearer` unless you have explicitly configured and validated Auth0 token exchange for your tenant.

MCP OAuth discovery endpoints (`/.well-known/oauth-authorization-server`, `/.well-known/oauth-protected-resource`, `/.well-known/openid-configuration`) are provider-aware based on `Jwt:Provider`. When `Jwt:Provider` is `Okta` or `Auth0`, these endpoints correctly advertise the Auth0 tenant's `/authorize`, `/oauth/token`, and JWKS URLs. Without this, discovery falls back to a hardcoded Microsoft Entra endpoint shape, which is incorrect for Auth0 tenants and can cause MCP-aware clients to attempt authorization against the wrong identity provider entirely.

### 4. Protected/downstream app runtime configuration

`C:\inetpub\wwwroot\OAuthClientCredsTestSite\jwtsettings.json`:

````````
{
  "Jwt": {
    "Authority": "https://login.microsoftonline.com/<tenant-id>/v2.0",
    "Audiences": [
      "api://<protected-api-app-id>"
    ],

````````

Both `jwtsettings.json` files must have valid, non-empty `Jwt:Authority` and `Jwt:Audiences` — an incomplete/empty file causes the app to crash on startup (`500.30`, `InvalidOperationException: JWT configuration is incomplete`).

Endpoint authorization checks should be permission-aware, not role-only, since Auth0 delegated tokens commonly carry a `permissions` claim (JSON array string) rather than a `roles` claim:

````````
`HasScopeOrPermission(...)` must parse claim values that may be JSON array strings (for example `["api.read","competitor.read"]`), not only space-delimited strings, or permission checks will silently fail even when the token carries the correct grant.

### 5. Microsoft 365 Copilot / Power Platform custom connector configuration

[TODO: confirm and document the exact working configuration — see notes below]

The path that ultimately resolved token audience/authentication issues combined:

1. A working custom connector `apiProperties.json` using `identityProvider: "oauth2generic"` with explicit `customParameters` (`AuthorizationUrl`, `TokenUrl`, `RefreshUrl`) plus `AuthorizationUrlTemplate` and `AuthorizationUrlQueryStringTemplate` to inject the MCP API audience into the authorization request, downloaded/updated via `pac connector download` / `pac connector update`.
2. Re-verifying the client secret directly in the Power Apps portal's connector **Security** tab after every `pac connector update` — the Power Platform CLI does not round-trip the client secret through `apiProperties.json`, and a stale/missing secret produces a generic `Unauthorized` token-exchange failure (Auth0 log event type `feacft`) that looks identical to an audience/scope misconfiguration.
3. [Add specific detail from Microsoft Learn plugin authentication OAuth guidance once confirmed — for example, whether this ultimately required an `ai-plugin.json` manifest `auth` block instead of/alongside the custom connector `apiProperties.json`.]

Reference `apiProperties.json` `customParameters` block:

````````

### 8. Common failures

- `401 Unauthorized` from MCP or app
  - Authority/issuer mismatch
  - Audience mismatch
  - `Audiences` written as scalar instead of JSON array

- `403 Forbidden` from MCP (no OBO trace entry written)
  - Incoming token missing required role/scope for the MCP API audience
  - `McpInvoke` policy or JWT bearer configuration missing/misconfigured in `OUathMCPServer/Program.cs`
  - Token is a JWE, not a signed JWT — check for `SecurityTokenDecryptionFailedException` in stdout logs, which means the connector requested the wrong (default/userinfo) audience during authorization

- `403 Forbidden` from downstream API (OBO trace entry present)
  - Downstream token validates, but required scope/role/permission missing for that specific endpoint
  - Auth0 `permissions` claim arrives as a JSON array string; ensure claim parsing handles that shape, not just space-delimited strings
  - Confirm both the OBO client and the signed-in user are granted the specific permission for that endpoint (both must be granted; either one missing causes a `403`)

- `Callback URL mismatch` during sign-in
  - The client's `redirect_uri` is not listed (exact match) in the Auth0 application's Allowed Callback URLs
  - Multiple redirect URIs (for example Bruno and a Power Platform connector) can coexist in the same list

- `access_denied` / `Unauthorized` during token exchange (Auth0 log event type `feacft`)
  - Check Auth0 Monitoring → Logs `description` field for the exact reason
  - A generic `Unauthorized` with no further detail is commonly caused by a missing/stale client secret — always re-verify the client secret in the Power Apps portal's connector Security tab after any `pac connector update`, since the CLI does not round-trip secrets through `apiProperties.json`
  - `Invalid subject_token_type` specifically indicates `Obo:GrantType` is set to `urn:ietf:params:oauth:grant-type:token-exchange` without valid tenant/API support; switch to `urn:ietf:params:oauth:grant-type:jwt-bearer`

- `Missing required parameter: client_id` during authorization
  - A custom `authorizationUrl` with a hand-appended query string (for example `?audience=...`) can be corrupted if the connector platform appends its own parameters using `?` instead of `&`. Do not hand-edit `authorizationUrl` with a static query string in Swagger `securityDefinitions`; use the connector's supported template mechanism instead (see section 5)

- `500.30 app failed to start`
  - Invalid, empty, or malformed runtime `jwtsettings.json`/`obosettings.json`
  - Confirm `Jwt:Authority` and `Jwt:Audiences` are present and correctly shaped in both apps' deployed config files
