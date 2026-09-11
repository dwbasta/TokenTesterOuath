# Deploying to IIS

These instructions assume you have already received the published website files, preferably as:

```text
OAuthClientCredsTestSite.zip
```

Run all commands below on the IIS server in **PowerShell opened as Administrator**.

## 1. Copy the files to the server

Copy these files to the server, for example into `C:\Deploy`:

```text
C:\Deploy\OAuthClientCredsTestSite.zip
C:\Deploy\deploy-iis-vm.ps1
C:\Deploy\request-letsencrypt-certificate.ps1
```

The deployment script installs/configures IIS, installs the .NET 8 Hosting Bundle only when the IIS ASP.NET Core Module is missing, creates the application pool and website, and copies the published files.

## 2. Run the deployment script

Update the hostname and run:

```powershell
C:\Deploy\deploy-iis-vm.ps1 `
  -SourcePath "C:\Deploy\OAuthClientCredsTestSite.zip" `
  -SiteName "OAuthClientCredsTestSite" `
  -AppPoolName "OAuthClientCredsTestSitePool" `
  -PhysicalPath "C:\inetpub\wwwroot\OAuthClientCredsTestSite" `
  -Port 80 `
  -HostName "your-hostname.example.com"
```

If the site is only being tested locally, omit `-HostName`:

```powershell
C:\Deploy\deploy-iis-vm.ps1 `
  -SourcePath "C:\Deploy\OAuthClientCredsTestSite.zip" `
  -PhysicalPath "C:\inetpub\wwwroot\OAuthClientCredsTestSite" `
  -Port 80
```

When updating an existing site, it adds the requested HTTP binding only if it is missing and preserves all existing HTTPS bindings and certificate configuration. To redeploy without adding or changing any HTTP binding, use:

```powershell
C:\Deploy\deploy-iis-vm.ps1 `
  -SourcePath "C:\Deploy\OAuthClientCredsTestSite.zip" `
  -SkipHttpBinding
```

`-SkipHttpBinding` is for an existing IIS site. A new site needs an initial binding. The script automatically skips the .NET 8 Hosting Bundle download when `IIS AspNetCore Module V2\aspnetcorev2.dll` is already installed.

## 3. Configure the API during deployment

When `jwtsettings.json` is not already configured, the deployment script prompts for:

- The Entra tenant ID
- The **Protected API app registration's Client ID** (not the calling client app's Client ID)

Example prompts:

```text
Enter the Entra tenant ID: <tenant-guid>
Enter the Protected API app registration Client ID: <protected-api-client-guid>
```

The script writes the values to:

```text
C:\inetpub\wwwroot\OAuthClientCredsTestSite\jwtsettings.json
```

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

```powershell
C:\Deploy\request-letsencrypt-certificate.ps1 `
  -SiteName "OAuthClientCredsTestSite" `
  -DnsName "example.contoso.xyz" `
  -EmailAddress "admin@example.com" `
  -WacsExe "C:\Tools\win-acme\wacs.exe"
```

The script first verifies that the IIS site exists and adds the HTTP host-header binding if needed. win-acme then requests and installs the certificate. After it completes, confirm that IIS has an HTTPS binding for the site:

```powershell
Import-Module WebAdministration
Get-WebBinding -Name "OAuthClientCredsTestSite"
```

Open the site using HTTPS:

```text
https://ResurgemusTokenTest.resurgemus.xyz/
```

## 6. Call the protected API

This deployment exposes a public status page at `/`, but it does not request or store client credentials. The API endpoints remain protected.

The repository includes `scripts\call-protected-api.ps1` for a direct test call. Edit the configuration values at the top of that script with the calling client app's tenant ID, client ID, client secret, protected API scope, and API URL. Do not commit a real client secret.

Run it with:

```powershell
C:\Deploy\call-protected-api.ps1
```

Your client application must:

1. Request a client-credentials token from Microsoft Entra ID using the protected API scope:

   ```text
   api://YOUR_PROTECTED_API_CLIENT_ID/.default
   ```

2. Have the protected API's `Api.Read` application permission assigned and consented.
3. Call the API with the token:

   ```http
   GET https://ResurgemusTokenTest.resurgemus.xyz/api/data
   Authorization: Bearer YOUR_ACCESS_TOKEN
   ```

A valid token returns HTTP `200 OK` with a joke secret-recipe payload:

```json
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
```

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

```json
{
  "Api": {
    "DisplayName": "My Protected Token API"
  }
}
```

The endpoint data is fictional demonstration data. Do not use the sample salary, address, phone, or customer information as real records.

## Troubleshooting

- **The script will not run:** Use an elevated PowerShell window and confirm that both the ZIP file and script path are correct.
- **The site returns HTTP 500:** Confirm that the .NET 8 Hosting Bundle was installed and that `jwtsettings.json` has the correct tenant (Authority) and audience.
- **The browser redirects but cannot connect:** Configure the IIS HTTPS binding and certificate.
- **The API returns 401:** Check the tenant, authority, audience, token issuer, and token expiration.
- **The API returns 403:** Confirm that the calling client has the protected API's `Api.Read` application permission and administrator consent.

## 7. Deploy and configure MCP with OBO for Copilot Studio

This section configures `OUathMCPServer` as an MCP endpoint that performs OBO token exchange to call the downstream protected API.

### 7.1 Publish both applications

Run from repository root:

```powershell
cd C:\Users\dylanbasta\source\repos\dwbasta\TokenTesterOuath

$publishRoot = "C:\temp\publish"
$webOut = Join-Path $publishRoot "OAuthClientCredsTestSite"
$mcpOut = Join-Path $publishRoot "OUathMCPServer"

New-Item -ItemType Directory -Path $webOut -Force | Out-Null
New-Item -ItemType Directory -Path $mOut -Force | Out-Null

dotnet restore .\OAuthClientCredsTestSite\OAuthClientCredsTestSite.csproj
dotnet restore .\OUathMCPServer\OUathMCPServer.csproj

dotnet build .\OAuthClientCredsTestSite\OAuthClientCredsTestSite.csproj -c Release
dotnet build .\OUathMCPServer\OUathMCPServer.csproj -c Release

dotnet publish .\OAuthClientCredsTestSite\OAuthClientCredsTestSite.csproj -c Release -o $webOut
dotnet publish .\OUathMCPServer\OUathMCPServer.csproj -c Release -o $mcpOut
```

Deploy `C:\temp\publish\OUathMCPServer` to the IIS site path for the MCP app.

### 7.2 Required MCP configuration files

`OUathMCPServer\jwtsettings.json`:

```json
{
  "Jwt": {
    "Authority": "https://login.microsoftonline.com/<tenant-id>/v2.0",
    "Audiences": [
      "api://8ac01e5d-5520-4952-b84c-5eecf1fece25"
    ],
    "RequiredScope": "access_as_user"
  }
}
```

`OUathMCPServer\obosettings.json`:

```json
{
  "Obo": {
    "ClientId": "<mcp-server-app-id>",
    "ClientSecret": "<mcp-server-app-secret>",
    "DownstreamApiBaseUrl": "https://ResurgemusTokenTest.resurgemus.xyz",
    "DownstreamScope": "api://404be116-6db5-481a-9974-56709134adb5/Competitor.Read"
  }
}
```

### 7.3 MCP endpoints expected by Copilot Studio

The MCP app should expose:

- `GET /.well-known/mcp`
- `POST /mcp`
- `GET /.well-known/oauth-protected-resource`
- `GET /.well-known/oauth-protected-resource/mcp`
- `GET /.well-known/oauth-authorization-server`
- `GET /.well-known/oauth-authorization-server/mcp`
- `GET /.well-known/openid-configuration`
- `GET /.well-known/openid-configuration/mcp`

### 7.4 Copilot Studio MCP setup (Manual OAuth)

Use **Manual** MCP configuration and these values:

- Server URL: `https://mcptokentest.resurgemus.xyz/mcp`
- Client ID: `52b8b893-bed0-4b14-91d0-39349f574f5d`
- Client secret: connector client app secret value
- Authorization URL: `https://login.microsoftonline.com/e0e1f74a-a300-42c6-a65c-917c4befb560/oauth2/v2.0/authorize`
- Token URL: `https://login.microsoftonline.com/e0e1f74a-a300-42c6-a65c-917c4befb560/oauth2/v2.0/token`
- Refresh token URL: `https://login.microsoftonline.com/e0e1f74a-a300-42c6-a65c-917c4befb560/oauth2/v2.0/token`
- Scope: `api://8ac01e5d-5520-4952-b84c-5eecf1fece25/access_as_user openid profile offline_access`

Required app registration configuration for client app `52b8b893-bed0-4b14-91d0-39349f574f5d`:

- Add the Copilot/APIM redirect URI shown during sign-in (for example `https://global.consent.azure-apim.net/redirect/...`).
- Add delegated permission to MCP API scope `access_as_user`.
- Grant admin/user consent.

### 7.5 Validation commands

```powershell
Invoke-RestMethod https://mcptokentest.resurgemus.xyz/api/status
Invoke-RestMethod https://mcptokentest.resurgemus.xyz/.well-known/mcp
Invoke-RestMethod https://mcptokentest.resurgemus.xyz/.well-known/oauth-protected-resource
Invoke-RestMethod https://mcptokentest.resurgemus.xyz/.well-known/oauth-authorization-server
Invoke-RestMethod https://mcptokentest.resurgemus.xyz/.well-known/openid-configuration
Invoke-RestMethod https://mcptokentest.resurgemus.xyz/.well-known/oauth-protected-resource/mcp
Invoke-RestMethod https://mcptokentest.resurgemus.xyz/.well-known/oauth-authorization-server/mcp
Invoke-RestMethod https://mcptokentest.resurgemus.xyz/.well-known/openid-configuration/mcp
Invoke-RestMethod -Method Post https://mcptokentest.resurgemus.xyz/mcp -ContentType "application/json" -Body '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}'
Invoke-RestMethod -Method Post https://mcptokentest.resurgemus.xyz/mcp -ContentType "application/json" -Body '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
```

### 7.6 Common MCP/OAuth failures

- **Could not discover authorization server metadata**: discovery/OAuth metadata endpoints missing or returning non-`200`.
- **GetDynamicClientRegistrationResultAsync failed (NotFound)**: dynamic client registration is not available; use Manual OAuth setup.
- **AADSTS50011 redirect URI mismatch**: add the exact redirect URI used by Copilot/APIM to the client app registration.
- **AADSTS7000218 invalid_client**: wrong client type/credentials or missing secret for confidential flow.
- **MCP tool load failure with 403**: token audience/scope mismatch (`aud` must be MCP API audience; `scp` must include `access_as_user`).
- **Tool runtime failure**: verify `Obo:DownstreamApiBaseUrl` DNS resolution and downstream API availability.

### 7.7 IIS log and event log checks

For recent MCP failures:

```powershell
Get-ChildItem "C:\inetpub\logs\LogFiles" -Recurse -File |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 3 FullName, LastWriteTime

Get-WinEvent -FilterHashtable @{
  LogName = "Application"
  StartTime = (Get-Date).AddMinutes(-30)
} | Where-Object {
  $_.ProviderName -match "IIS|ASP.NET|.NET Runtime|IIS AspNetCore Module V2"
} | Select-Object TimeCreated, ProviderName, LevelDisplayName, Id, Message -First 80
```

## Security notes

- Never commit real client secrets or refresh tokens to source control.
- Rotate any secret/token that has been exposed in terminal history or logs.
- Prefer environment variables or secure secret stores for production deployments.
