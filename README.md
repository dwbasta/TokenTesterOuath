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
