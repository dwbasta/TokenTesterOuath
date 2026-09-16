param(
    [string]$TenantId = "e0e1f74a-a300-42c6-a65c-917c4befb560",
    [string]$ClientId = "52b8b893-bed0-4b14-91d0-39349f574f5d",
    [string]$ClientSecret = "0hh8Q~LgsgP4JbcKGpAXEB75iGcgL1w11hWd9bUF",
    [string]$RedirectUri = "http://localhost:8400/callback",
    [string]$Scope = "api://8ac01e5d-5520-4952-b84c-5eecf1fece25/access_as_user openid profile offline_access",
    [switch]$CopyToClipboard
)

$ErrorActionPreference = "Stop"

function Get-JwtPayload {
    param([Parameter(Mandatory = $true)][string]$Jwt)

    $parts = $Jwt.Split(".")
    if ($parts.Length -lt 2) {
        throw "Invalid JWT format."
    }

    $payload = $parts[1].Replace("-", "+").Replace("_", "/")
    switch ($payload.Length % 4) {
        2 { $payload += "==" }
        3 { $payload += "=" }
    }

    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
}

if ([string]::IsNullOrWhiteSpace($ClientSecret)) {
    throw "ClientSecret is required. Pass -ClientSecret '<secret value>'."
}

$authorizeEndpoint = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/authorize"
$tokenEndpoint = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"

if (-not [Uri]::TryCreate($authorizeEndpoint, [UriKind]::Absolute, [ref]([Uri]$null))) {
    throw "Authorize endpoint invalid: $authorizeEndpoint"
}
if (-not [Uri]::TryCreate($tokenEndpoint, [UriKind]::Absolute, [ref]([Uri]$null))) {
    throw "Token endpoint invalid: $tokenEndpoint"
}

$state = [Guid]::NewGuid().ToString("N")

$authBuilder = [System.UriBuilder]::new($authorizeEndpoint)
$query = [System.Web.HttpUtility]::ParseQueryString([string]::Empty)
$query["client_id"] = $ClientId
$query["response_type"] = "code"
$query["redirect_uri"] = $RedirectUri
$query["response_mode"] = "query"
$query["scope"] = $Scope
$query["state"] = $state
$query["prompt"] = "select_account"
$authBuilder.Query = $query.ToString()
$authUrl = $authBuilder.Uri.AbsoluteUri

$redirect = [Uri]$RedirectUri
$listenerPrefix = "{0}://{1}:{2}/" -f $redirect.Scheme, $redirect.Host, $redirect.Port
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add($listenerPrefix)
$listener.Start()

Write-Host "Authorize endpoint: $authorizeEndpoint" -ForegroundColor DarkGray
Write-Host "Token endpoint: $tokenEndpoint" -ForegroundColor DarkGray
Write-Host "Open sign-in URL:" -ForegroundColor Cyan
Write-Host $authUrl -ForegroundColor Yellow

try {
    Start-Process -FilePath $authUrl | Out-Null
}
catch {
    Write-Warning "Automatic browser launch failed. Open the URL manually."
    try {
        Set-Clipboard -Value $authUrl
        Write-Host "Auth URL copied to clipboard." -ForegroundColor Cyan
    }
    catch {
        Write-Warning "Could not copy URL to clipboard."
    }
}

Write-Host "Waiting for redirect on $listenerPrefix ..." -ForegroundColor Cyan
$context = $listener.GetContext()
$request = $context.Request
$response = $context.Response

$html = @"
<html><body><h2>Sign-in complete.</h2><p>You can close this window.</p></body></html>
"@
$buffer = [Text.Encoding]::UTF8.GetBytes($html)
$response.ContentLength64 = $buffer.Length
$response.OutputStream.Write($buffer, 0, $buffer.Length)
$response.OutputStream.Close()
$listener.Stop()

$queryResponse = [System.Web.HttpUtility]::ParseQueryString($request.Url.Query)
$returnedState = $queryResponse["state"]
$code = $queryResponse["code"]
$error = $queryResponse["error"]
$errorDescription = $queryResponse["error_description"]

if (-not [string]::IsNullOrWhiteSpace($error)) {
    throw "Authorization failed: $error - $errorDescription"
}

if ($returnedState -ne $state) {
    throw "State mismatch in authorization response."
}

if ([string]::IsNullOrWhiteSpace($code)) {
    throw "Authorization code not found in redirect response."
}

Write-Host "Exchanging authorization code for token..." -ForegroundColor Cyan
$tokenResponse = Invoke-RestMethod -Method Post `
    -Uri $tokenEndpoint `
    -ContentType "application/x-www-form-urlencoded" `
    -Body @{
        client_id = $ClientId
        client_secret = $ClientSecret
        grant_type = "authorization_code"
        code = $code
        redirect_uri = $RedirectUri
        scope = $Scope
    }

$accessToken = $tokenResponse.access_token
if ([string]::IsNullOrWhiteSpace($accessToken)) {
    throw "No access_token returned."
}

$claims = Get-JwtPayload -Jwt $accessToken
$expUtc = [DateTimeOffset]::FromUnixTimeSeconds([long]$claims.exp).UtcDateTime

Write-Host ""
Write-Host "Token acquired." -ForegroundColor Green
Write-Host "aud: $($claims.aud)"
Write-Host "scp: $($claims.scp)"
Write-Host "exp (UTC): $expUtc"
Write-Host ""

if ($CopyToClipboard) {
    Set-Clipboard -Value $accessToken
    Write-Host "Access token copied to clipboard." -ForegroundColor Cyan
}

[PSCustomObject]@{
    access_token = $accessToken
    expires_on_utc = $expUtc
    aud = $claims.aud
    scp = $claims.scp
}