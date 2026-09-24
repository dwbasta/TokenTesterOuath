[CmdletBinding()]
param(
    [string]$ProtectedAppPath = "C:\inetpub\wwwroot\OAuthClientCredsTestSite",
    [string]$McpPath = "C:\inetpub\wwwroot\OUathMCPServer"
)

$ErrorActionPreference = "Stop"

function Read-Required([string]$Prompt) {
    while ($true) {
        $value = (Read-Host $Prompt).Trim()
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value
        }
        Write-Host "Value is required." -ForegroundColor Yellow
    }
}

function Ensure-Path([string]$Path) {
    if (-not (Test-Path $Path)) {
        throw "Path not found: $Path"
    }
}

Ensure-Path $ProtectedAppPath
Ensure-Path $McpPath

$providerRaw = Read-Required "Identity provider (Entra/Okta)"
$provider = $providerRaw.ToLowerInvariant()

switch ($provider) {
    "entra" {
        $tenantId = Read-Required "Entra tenant ID (GUID)"
        $protectedApiClientId = Read-Required "Protected App Client ID (GUID)"
        $mcpApiClientId = Read-Required "MCP API Client ID (GUID)"
        $mcpClientSecret = Read-Required "MCP confidential client secret"
        $downstreamApiBaseUrl = Read-Required "Downstream API base URL (for MCP OBO)"
        $downstreamScope = Read-Required "Downstream scope (example: api://<protected-app-id>/Api.Read)"

        $authority = "https://login.microsoftonline.com/$tenantId/v2.0"
        $validIssuers = @(
            "https://login.microsoftonline.com/$tenantId/v2.0",
            "https://sts.windows.net/$tenantId/"
        )
        $mcpAudience = "api://$mcpApiClientId"
        $protectedAudience = "api://$protectedApiClientId"
        $oboTokenEndpoint = "https://login.microsoftonline.com/$tenantId/oauth2/v2.0/token"
        $oboGrantType = "urn:ietf:params:oauth:grant-type:jwt-bearer"
    }
    "okta" {
        $oktaDomain = Read-Required "Okta domain (example: https://dev-123456.okta.com)"
        $oktaAuthorizationServerId = Read-Required "Okta authorization server id (example: default)"
        $mcpAudience = Read-Required "MCP audience (example: api://mcp-server)"
        $protectedAudience = Read-Required "Protected API audience (example: api://protected-api)"
        $mcpClientId = Read-Required "MCP confidential client id"
        $mcpClientSecret = Read-Required "MCP confidential client secret"
        $downstreamApiBaseUrl = Read-Required "Downstream API base URL (for MCP token exchange)"
        $downstreamScope = Read-Required "Downstream scope(s) (space-separated if multiple, example: api.read)"

        $authority = "$($oktaDomain.TrimEnd('/'))/oauth2/$oktaAuthorizationServerId"
        $validIssuers = @($authority)
        $oboTokenEndpoint = "$authority/v1/token"
        $oboGrantType = "urn:ietf:params:oauth:grant-type:token-exchange"
    }
    default {
        throw "Unsupported provider: $providerRaw. Use Entra or Okta."
    }
}

$protectedJwt = [ordered]@{
    Jwt = [ordered]@{
        Provider = if ($provider -eq "entra") { "Entra" } else { "Okta" }
        Authority = $authority
        ValidIssuers = $validIssuers
        Audiences = @($protectedAudience)
        RequiredRole = "Api.Read"
        RequiredWriteRole = "Api.Write"
        RoleClaimType = "roles"
        NameClaimType = if ($provider -eq "entra") { "appid" } else { "sub" }
    }
}

$mcpJwt = [ordered]@{
    Jwt = [ordered]@{
        Provider = if ($provider -eq "entra") { "Entra" } else { "Okta" }
        Authority = $authority
        ValidIssuers = $validIssuers
        Audiences = @($mcpAudience)
        RequiredRole = "Mcp.Invoke"
        RequiredScope = "access_as_user"
        RoleClaimType = "roles"
        NameClaimType = if ($provider -eq "entra") { "oid" } else { "sub" }
    }
}

$mcpObo = [ordered]@{
    Obo = [ordered]@{
        ClientId = if ($provider -eq "entra") { $mcpApiClientId } else { $mcpClientId }
        ClientSecret = $mcpClientSecret
        DownstreamApiBaseUrl = $downstreamApiBaseUrl
        DownstreamScope = $downstreamScope
        DownstreamAudience = $protectedAudience
        TokenEndpoint = $oboTokenEndpoint
        GrantType = $oboGrantType
        WriteTokenTraceFile = $true
        TokenTraceFilePath = "logs/obo-token-trace.jsonl"
    }
}

Set-Content -Path (Join-Path $ProtectedAppPath "jwtsettings.json") -Value ($protectedJwt | ConvertTo-Json -Depth 10) -Encoding UTF8
Set-Content -Path (Join-Path $McpPath "jwtsettings.json") -Value ($mcpJwt | ConvertTo-Json -Depth 10) -Encoding UTF8
Set-Content -Path (Join-Path $McpPath "obosettings.json") -Value ($mcpObo | ConvertTo-Json -Depth 10) -Encoding UTF8

Write-Host ""
Write-Host "Identity provider configuration written successfully." -ForegroundColor Green
Write-Host "Protected API: $(Join-Path $ProtectedAppPath "jwtsettings.json")"
Write-Host "MCP JWT:      $(Join-Path $McpPath "jwtsettings.json")"
Write-Host "MCP OBO:      $(Join-Path $McpPath "obosettings.json")"