[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath, # Published folder or .zip package

    [string]$SiteName = "OAuthClientCredsTestSite",
    [string]$AppPoolName = "OAuthClientCredsTestSitePool",
    [string]$PhysicalPath = "C:\inetpub\wwwroot\OAuthClientCredsTestSite",
    [int]$Port = 80,
    [string]$HostName = "",

    [string]$IdentityProvider = "", # Entra | Okta
    [string]$TenantId = "",
    [string]$ProtectedApiClientId = "",
    [string]$OktaAuthority = "",
    [string]$OktaAudience = "",
    [string]$OktaValidIssuersCsv = "",

    [switch]$OverwriteRuntimeConfig, # <-- add this
    [switch]$InstallHostingBundle = $true,
    [switch]$SkipHttpBinding,
    [string]$HostingBundleUrl = "https://aka.ms/dotnet/8.0/dotnet-hosting-win.exe"
)

$ErrorActionPreference = "Stop"

function Test-IsAdmin {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-Setting {
    param(
        [string]$CurrentValue,
        [string]$ExistingValue,
        [string]$Prompt
    )

    if (-not [string]::IsNullOrWhiteSpace($CurrentValue)) {
        return $CurrentValue.Trim()
    }

    if (-not [string]::IsNullOrWhiteSpace($ExistingValue)) {
        return $ExistingValue.Trim()
    }

    return (Read-Host $Prompt).Trim()
}

function Resolve-IdentityProvider {
    param(
        [string]$CurrentProvider,
        [string]$ExistingProvider,
        [string]$ExistingAuthority
    )

    $candidate = ""
    if (-not [string]::IsNullOrWhiteSpace($CurrentProvider)) {
        $candidate = $CurrentProvider.Trim()
    }
    elseif (-not [string]::IsNullOrWhiteSpace($ExistingProvider)) {
        $candidate = $ExistingProvider.Trim()
    }
    elseif (-not [string]::IsNullOrWhiteSpace($ExistingAuthority)) {
        if ($ExistingAuthority -match "login\.microsoftonline\.com") {
            $candidate = "Entra"
        }
        elseif ($ExistingAuthority -match "/oauth2/") {
            $candidate = "Okta"
        }
    }
    else {
        $candidate = (Read-Host "Identity provider (Entra/Okta)").Trim()
    }

    if ([string]::IsNullOrWhiteSpace($candidate)) {
        throw "Identity provider is required. Enter Entra or Okta."
    }

    switch ($candidate.ToLowerInvariant()) {
        "entra" { return "Entra" }
        "okta" { return "Okta" }
        default { throw "Unsupported identity provider '$candidate'. Use Entra or Okta." }
    }
}

function Split-CsvValues {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return @()
    }

    return @(
        $Value.Split(",") |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
}

function Resolve-HttpBindingConflict {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DesiredBinding,
        [Parameter(Mandatory = $true)]
        [string]$TargetSiteName
    )

    $conflicts = Get-WebBinding -Protocol http |
        Where-Object {
            $_.bindingInformation -eq $DesiredBinding -and
            $_.ItemXPath.Split("'")[1] -ne $TargetSiteName
        }

    if ($null -eq $conflicts -or $conflicts.Count -eq 0) {
        return
    }

    $conflictSites = $conflicts | ForEach-Object { $_.ItemXPath.Split("'")[1] } | Select-Object -Unique
    $nonDefault = $conflictSites | Where-Object { $_ -ne "Default Web Site" }

    if ($nonDefault.Count -gt 0) {
        throw "HTTP binding conflict: '$DesiredBinding' is already used by site(s): $($conflictSites -join ', ')."
    }

    Write-Host "==> Removing conflicting HTTP binding(s) from 'Default Web Site': $DesiredBinding" -ForegroundColor Yellow

    foreach ($binding in $conflicts) {
        $parts = $binding.bindingInformation.Split(':', 3)
        $ip = $parts[0]
        $bindingPort = [int]$parts[1]
        $hostHeader = $parts[2]
        Remove-WebBinding -Name "Default Web Site" -Protocol http -Port $bindingPort -IPAddress $ip -HostHeader $hostHeader
    }

    $defaultSite = Get-Website -Name "Default Web Site" -ErrorAction SilentlyContinue
    if ($null -ne $defaultSite -and $defaultSite.State -eq "Started") {
        Write-Host "==> Stopping 'Default Web Site'..." -ForegroundColor Yellow
        Stop-Website -Name "Default Web Site"
    }

    Set-ItemProperty "IIS:\Sites\Default Web Site" -Name serverAutoStart -Value $false
}

if (-not (Test-IsAdmin)) {
    throw "Run this script in an elevated PowerShell session (Run as Administrator)."
}

Write-Host "==> Installing IIS features..."
if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
    Install-WindowsFeature Web-Server, Web-WebServer, Web-Common-Http, Web-Static-Content, Web-Default-Doc, Web-Http-Errors, Web-Http-Logging, Web-Request-Monitor, Web-Performance, Web-Stat-Compression, Web-Security, Web-Filtering, Web-App-Dev, Web-Net-Ext45, Web-Asp-Net45, Web-Mgmt-Tools -IncludeManagementTools | Out-Null
}
else {
    Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole,IIS-WebServer,IIS-CommonHttpFeatures,IIS-StaticContent,IIS-DefaultDocument,IIS-HttpErrors,IIS-HttpLogging,IIS-RequestFiltering,IIS-ManagementConsole -All -NoRestart | Out-Null
}

$hostingModulePath = Join-Path ${env:ProgramFiles} "IIS AspNetCore Module V2\aspnetcorev2.dll"
if ($InstallHostingBundle -and (Test-Path $hostingModulePath)) {
    Write-Host "==> .NET 8 Hosting Bundle is already installed; skipping installation."
}
elseif ($InstallHostingBundle) {
    Write-Host "==> Installing .NET 8 Hosting Bundle..."
    $installer = Join-Path $env:TEMP "dotnet-hosting-win.exe"
    Invoke-WebRequest -Uri $HostingBundleUrl -OutFile $installer
    $process = Start-Process -FilePath $installer -ArgumentList "/install /quiet /norestart" -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        Remove-Item -Path $installer -Force -ErrorAction SilentlyContinue
        throw ".NET 8 Hosting Bundle installation failed with exit code $($process.ExitCode). Verify the installer URL and system prerequisites."
    }
    Remove-Item -Path $installer -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path $SourcePath)) {
    throw "SourcePath not found: $SourcePath"
}

$jwtSettingsPath = Join-Path $PhysicalPath "jwtsettings.json"
$jwtSettingsContent = $null
$existingJwtSettings = $null

if (Test-Path $jwtSettingsPath) {
    try {
        $existingJwtSettings = Get-Content -Path $jwtSettingsPath -Raw | ConvertFrom-Json
    }
    catch {
        $existingJwtSettings = $null
    }
}

$existingProvider = ""
$existingAuthority = ""
$existingAudience = ""
$existingValidIssuersCsv = ""

if ($null -ne $existingJwtSettings -and $null -ne $existingJwtSettings.Jwt) {
    if ($null -ne $existingJwtSettings.Jwt.Provider) {
        $existingProvider = $existingJwtSettings.Jwt.Provider.ToString()
    }
    if ($null -ne $existingJwtSettings.Jwt.Authority) {
        $existingAuthority = $existingJwtSettings.Jwt.Authority.ToString()
    }

    $existingAudiences = @($existingJwtSettings.Jwt.Audiences)
    if ($existingAudiences.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($existingAudiences[0])) {
        $existingAudience = $existingAudiences[0].ToString()
    }

    $existingValidIssuers = @($existingJwtSettings.Jwt.ValidIssuers)
    if ($existingValidIssuers.Count -gt 0) {
        $existingValidIssuersCsv = ($existingValidIssuers -join ",")
    }
}

$IdentityProvider = Resolve-IdentityProvider -CurrentProvider $IdentityProvider -ExistingProvider $existingProvider -ExistingAuthority $existingAuthority

$resolvedAuthority = ""
$resolvedAudiences = @()
$resolvedValidIssuers = @()

if ($IdentityProvider -eq "Entra") {
    $TenantId = Resolve-Setting -CurrentValue $TenantId -ExistingValue "" -Prompt "Enter the Entra tenant ID"

    if ($TenantId -like "https://login.microsoftonline.com/*/v2.0") {
        $TenantId = ($TenantId -replace "^https://login\.microsoftonline\.com/", "") -replace "/v2\.0$", ""
    }

    if ([string]::IsNullOrWhiteSpace($existingAudience)) {
        $existingAudience = ""
    }
    else {
        $existingAudience = $existingAudience -replace "^api://", ""
    }

    $ProtectedApiClientId = Resolve-Setting -CurrentValue $ProtectedApiClientId -ExistingValue $existingAudience -Prompt "Enter the Protected API app registration Client ID"

    $parsedTenantId = [guid]::Empty
    $parsedApiClientId = [guid]::Empty
    if (-not [guid]::TryParse($TenantId.Trim(), [ref]$parsedTenantId) -or
        -not [guid]::TryParse($ProtectedApiClientId.Trim(), [ref]$parsedApiClientId)) {
        throw "Tenant ID and Protected API Client ID must both be valid GUIDs for Entra."
    }

    $resolvedAuthority = "https://login.microsoftonline.com/$($TenantId.Trim())/v2.0"
    $resolvedAudiences = @("api://$($ProtectedApiClientId.Trim())")
    $resolvedValidIssuers = @(
        "https://login.microsoftonline.com/$($TenantId.Trim())/v2.0",
        "https://sts.windows.net/$($TenantId.Trim())/"
    )
}
else {
    $OktaAuthority = Resolve-Setting -CurrentValue $OktaAuthority -ExistingValue $existingAuthority -Prompt "Enter Okta authority/issuer URL (example: https://dev-123456.okta.com/oauth2/default)"
    $OktaAudience = Resolve-Setting -CurrentValue $OktaAudience -ExistingValue $existingAudience -Prompt "Enter protected API audience (example: api://protected-api)"

    if (-not [Uri]::TryCreate($OktaAuthority, [UriKind]::Absolute, [ref]([Uri]$null))) {
        throw "OktaAuthority must be a valid absolute URI."
    }

    $resolvedAuthority = $OktaAuthority.TrimEnd('/')
    $resolvedAudiences = @(Split-CsvValues -Value $OktaAudience)
    if ($resolvedAudiences.Count -eq 0) {
        throw "At least one Okta audience is required."
    }

    $effectiveIssuersCsv = Resolve-Setting -CurrentValue $OktaValidIssuersCsv -ExistingValue $existingValidIssuersCsv -Prompt "Enter valid issuers CSV (leave blank to use authority only)"
    $resolvedValidIssuers = Split-CsvValues -Value $effectiveIssuersCsv
    if ($resolvedValidIssuers.Count -eq 0) {
        $resolvedValidIssuers = @($resolvedAuthority)
    }
}

$jwtSettingsContent = [ordered]@{
    Jwt = [ordered]@{
        Provider = $IdentityProvider
        Authority = $resolvedAuthority
        ValidIssuers = @($resolvedValidIssuers)
        Audiences = @($resolvedAudiences)
    }
} | ConvertTo-Json -Depth 6

Write-Host "==> Configuring IIS site..."
Import-Module WebAdministration

if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
    New-WebAppPool -Name $AppPoolName | Out-Null
}
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion -Value ""
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedPipelineMode -Value "Integrated"

$existingSite = Get-Website -Name $SiteName -ErrorAction SilentlyContinue
if ($null -ne $existingSite) {
    Write-Host "==> Existing IIS site found; updating it in place..."

    if ($existingSite.State -eq "Started") {
        Stop-Website -Name $SiteName
    }

    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $PhysicalPath
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $AppPoolName

    $httpsBindings = @(Get-WebBinding -Name $SiteName -Protocol https)
    if ($httpsBindings.Count -gt 0) {
        Write-Host "==> Leaving $($httpsBindings.Count) existing HTTPS binding(s) and certificate configuration unchanged."
    }
}

$appPoolState = (Get-WebAppPoolState -Name $AppPoolName).Value
if ($appPoolState -eq "Started") {
    Write-Host "==> Stopping application pool before replacing locked files..."
    Stop-WebAppPool -Name $AppPoolName
}

for ($attempt = 0; $attempt -lt 30; $attempt++) {
    $appPoolState = (Get-WebAppPoolState -Name $AppPoolName).Value
    if ($appPoolState -eq "Stopped") {
        break
    }

    Start-Sleep -Seconds 1
}

if ((Get-WebAppPoolState -Name $AppPoolName).Value -ne "Stopped") {
    throw "Application pool '$AppPoolName' did not stop. Files may still be in use."
}

Write-Host "==> Waiting for IIS worker processes to release file locks..."
Start-Sleep -Seconds 5

function Copy-DeploymentContent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    for ($copyAttempt = 1; $copyAttempt -le 5; $copyAttempt++) {
        try {
            Copy-Item -Path $Path -Destination $Destination -Recurse -Force -ErrorAction Stop
            return
        }
        catch [System.IO.IOException] {
            if ($copyAttempt -eq 5) {
                throw
            }

            Write-Host "File still locked; retrying copy in 3 seconds (attempt $copyAttempt of 5)..." -ForegroundColor Yellow
            Start-Sleep -Seconds 3
        }
    }
}

Write-Host "==> Preparing website content..."
if (Test-Path $PhysicalPath) {
    Remove-Item "$PhysicalPath\*" -Recurse -Force -ErrorAction SilentlyContinue
}
else {
    New-Item -ItemType Directory -Path $PhysicalPath -Force | Out-Null
}

if ($SourcePath.ToLowerInvariant().EndsWith(".zip")) {
    $extractPath = Join-Path $env:TEMP ("deploy_" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $extractPath -Force | Out-Null

    try {
        Expand-Archive -Path $SourcePath -DestinationPath $extractPath -Force

        $rootItems = Get-ChildItem -Path $extractPath
        if ($rootItems.Count -eq 1 -and $rootItems[0].PSIsContainer) {
            Copy-DeploymentContent -Path (Join-Path $rootItems[0].FullName "*") -Destination $PhysicalPath
        }
        else {
            Copy-DeploymentContent -Path (Join-Path $extractPath "*") -Destination $PhysicalPath
        }
    }
    finally {
        Remove-Item $extractPath -Recurse -Force -ErrorAction SilentlyContinue
    }
}
else {
    Copy-DeploymentContent -Path (Join-Path $SourcePath "*") -Destination $PhysicalPath
}

if ((Test-Path $jwtSettingsPath) -and -not $OverwriteRuntimeConfig) {
    Write-Host "==> Preserving existing jwtsettings.json (use -OverwriteRuntimeConfig to replace)." -ForegroundColor Yellow
}
else {
    Set-Content -Path $jwtSettingsPath -Value $jwtSettingsContent -Encoding UTF8
    Write-Host "==> Wrote jwtsettings.json"
}

$desiredHttpBinding = ""
if (-not $SkipHttpBinding) {
    $desiredHttpBinding = if ([string]::IsNullOrWhiteSpace($HostName)) {
        "*:${Port}:"
    }
    else {
        "*:${Port}:$HostName"
    }

    Resolve-HttpBindingConflict -DesiredBinding $desiredHttpBinding -TargetSiteName $SiteName
}

if ($null -eq $existingSite) {
    if ($SkipHttpBinding) {
        throw "Cannot create a new IIS site while -SkipHttpBinding is set. Create the site and its HTTPS binding first, or omit -SkipHttpBinding for the initial deployment."
    }

    if ([string]::IsNullOrWhiteSpace($HostName)) {
        New-Website -Name $SiteName -PhysicalPath $PhysicalPath -Port $Port -ApplicationPool $AppPoolName | Out-Null
    }
    else {
        New-Website -Name $SiteName -PhysicalPath $PhysicalPath -Port $Port -HostHeader $HostName -ApplicationPool $AppPoolName | Out-Null
    }
}
else {
    if ($SkipHttpBinding) {
        Write-Host "==> Leaving all existing HTTP and HTTPS bindings unchanged."
    }
    else {
        $httpBindingExists = Get-WebBinding -Name $SiteName -Protocol http |
            Where-Object { $_.bindingInformation -eq $desiredHttpBinding }

        if ($null -eq $httpBindingExists) {
            if ([string]::IsNullOrWhiteSpace($HostName)) {
                New-WebBinding -Name $SiteName -Protocol http -Port $Port -IPAddress "*" | Out-Null
            }
            else {
                New-WebBinding -Name $SiteName -Protocol http -Port $Port -IPAddress "*" -HostHeader $HostName | Out-Null
            }
        }
        else {
            Write-Host "==> Existing HTTP binding preserved: $desiredHttpBinding"
        }
    }
}

Write-Host "==> Setting folder permissions..."
icacls $PhysicalPath /grant "IIS AppPool\${AppPoolName}:(OI)(CI)(RX)" /T | Out-Null

if ((Get-WebAppPoolState -Name $AppPoolName).Value -ne "Started") {
    Start-WebAppPool -Name $AppPoolName
}

if ((Get-Website -Name $SiteName).State -ne "Started") {
    Start-Website -Name $SiteName
}

Write-Host ""
Write-Host "Deployment complete."
Write-Host "Identity provider: $IdentityProvider"
if ([string]::IsNullOrWhiteSpace($HostName)) {
    Write-Host "URL: http://localhost:$Port/"
}
else {
    Write-Host "URL: http://$HostName`:$Port/"
}