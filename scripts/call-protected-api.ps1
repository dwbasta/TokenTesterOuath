param(
    [string]$TenantId = "YOUR_TENANT_ID",
    [string]$ClientId = "YOUR_CALLING_CLIENT_APP_ID",
    [string]$ClientSecret = "YOUR_CALLING_CLIENT_APP_SECRET",
    [string]$Scope = "api://YOUR_PROTECTED_API_APP_ID/.default",
    [string]$ApiUrl = "https://ResurgemusTokenTest.resurgemus.xyz/api/data",
    [string]$LogFile = "$PSScriptRoot\api-test-$(Get-Date -Format 'yyyy-MM-dd_HH-mm-ss').log"
)

function Write-Log {
    param([string]$Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $logLine = "[$timestamp] $Message"
    Write-Host $logLine
    Add-Content -Path $LogFile -Value $logLine -Encoding UTF8
}

# Local test configuration. Do not commit real client secrets to source control.
# You can edit the defaults above or override them when invoking this script.
$placeholderValues = @(
    "YOUR_TENANT_ID",
    "YOUR_CALLING_CLIENT_APP_ID",
    "YOUR_CALLING_CLIENT_APP_SECRET",
    "YOUR_PROTECTED_API_APP_ID"
)

if ($placeholderValues | Where-Object { $TenantId -like "*$_*" -or $ClientId -like "*$_*" -or $ClientSecret -like "*$_*" -or $Scope -like "*$_*" }) {
    throw "Edit the client configuration values at the top of call-protected-api.ps1 before running it."
}

$ErrorActionPreference = "Stop"

Write-Log "=== API Test Session Started ==="
Write-Log "Tenant ID: $TenantId"
Write-Log "Client ID: $ClientId"
Write-Log "Scope: $Scope"
Write-Log "API URL: $ApiUrl"
Write-Log "Log file: $LogFile"

$tokenEndpoint = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"
Write-Log "Token endpoint: $tokenEndpoint"

$tokenBody = @{
    client_id     = $ClientId
    client_secret = $ClientSecret
    scope         = $Scope
    grant_type    = "client_credentials"
}

Write-Log "Requesting token from Entra..."
try {
    $tokenResponse = Invoke-RestMethod -Method Post -Uri $tokenEndpoint -Body $tokenBody -ContentType "application/x-www-form-urlencoded"
    Write-Log "✓ Token acquired successfully"
    Write-Log "Token expires in: $($tokenResponse.expires_in) seconds"
} catch {
    Write-Log "✗ Token request failed: $($_.Exception.Message)"
    Write-Log "Response: $($_.ErrorDetails.Message)"
    throw
}

if (-not $tokenResponse.access_token) {
    Write-Log "✗ No access token in response"
    throw "No access token returned."
}

function Get-JwtPayload {
    param([Parameter(Mandatory = $true)][string]$Token)

    $parts = $Token.Split('.')
    if ($parts.Count -ne 3) {
        throw "The returned value is not a JWT."
    }

    $payload = $parts[1].Replace('-', '+').Replace('_', '/')
    while ($payload.Length % 4) {
        $payload += '='
    }

    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
}

try {
    $claims = Get-JwtPayload -Token $tokenResponse.access_token
    Write-Log "Token issuer (iss): $($claims.iss)"
    Write-Log "Token audience (aud): $($claims.aud)"
    Write-Log "Token roles: $((@($claims.roles) -join ', '))"
    Write-Log "Token scopes (scp): $($claims.scp)"
    Write-Log "Token application ID (appid): $($claims.appid)"
    Write-Log "Token expiration (exp): $($claims.exp)"
} catch {
    Write-Log "Could not decode token claims: $($_.Exception.Message)"
}

Write-Log "Calling protected API..."
$headers = @{
    Authorization = "Bearer $($tokenResponse.access_token)"
}

try {
    $apiResponse = Invoke-RestMethod -Method Get -Uri $ApiUrl -Headers $headers
    Write-Log "✓ API call succeeded"
    Write-Log "Response:"
    $apiResponse | ConvertTo-Json -Depth 10 | ForEach-Object { Write-Log $_ }
} catch {
    Write-Log "✗ API call failed with status: $($_.Exception.Response.StatusCode)"
    Write-Log "Error message: $($_.Exception.Message)"
    Write-Log "Response body: $($_.ErrorDetails.Message)"
    throw
}