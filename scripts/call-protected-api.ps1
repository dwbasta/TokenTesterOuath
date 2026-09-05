param(
    [Parameter(Mandatory = $true)]
    [string]$TenantId,

    [Parameter(Mandatory = $true)]
    [string]$ClientId,

    [Parameter(Mandatory = $true)]
    [string]$ClientSecret,

    [Parameter(Mandatory = $true)]
    [string]$Scope, # Example: api://<protected-api-client-id>/.default

    [Parameter(Mandatory = $true)]
    [string]$ApiUrl # Example: https://yourvm.contoso.com/api/data
)

$ErrorActionPreference = "Stop"

$tokenEndpoint = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"

$tokenBody = @{
    client_id     = $ClientId
    client_secret = $ClientSecret
    scope         = $Scope
    grant_type    = "client_credentials"
}

Write-Host "Requesting token from Entra..."
$tokenResponse = Invoke-RestMethod -Method Post -Uri $tokenEndpoint -Body $tokenBody -ContentType "application/x-www-form-urlencoded"

if (-not $tokenResponse.access_token) {
    throw "No access token returned."
}

Write-Host "Calling protected API..."
$headers = @{
    Authorization = "Bearer $($tokenResponse.access_token)"
}

$apiResponse = Invoke-RestMethod -Method Get -Uri $ApiUrl -Headers $headers
$apiResponse | ConvertTo-Json -Depth 10