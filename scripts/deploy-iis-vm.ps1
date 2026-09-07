[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath, # Published folder or .zip package

    [string]$SiteName = "OAuthClientCredsTestSite",
    [string]$AppPoolName = "OAuthClientCredsTestSitePool",
    [string]$PhysicalPath = "C:\inetpub\wwwroot\OAuthClientCredsTestSite",
    [int]$Port = 80,
    [string]$HostName = "",
    [string]$TenantId = "",
    [string]$ProtectedApiClientId = "",
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
$jwtConfigured = $false

if (Test-Path $jwtSettingsPath) {
    $existingJwtSettings = Get-Content -Path $jwtSettingsPath -Raw | ConvertFrom-Json
    $jwtConfigured = -not [string]::IsNullOrWhiteSpace($existingJwtSettings.Jwt.Authority) -and
        @($existingJwtSettings.Jwt.Audiences).Count -gt 0 -and
        -not [string]::IsNullOrWhiteSpace($existingJwtSettings.Jwt.Audiences[0])

    if ($jwtConfigured -and [string]::IsNullOrWhiteSpace($TenantId) -and [string]::IsNullOrWhiteSpace($ProtectedApiClientId)) {
        $jwtSettingsContent = Get-Content -Path $jwtSettingsPath -Raw
    }
}

if ($null -eq $jwtSettingsContent) {
    if ([string]::IsNullOrWhiteSpace($TenantId)) {
        $TenantId = Read-Host "Enter the Entra tenant ID"
    }

    if ([string]::IsNullOrWhiteSpace($ProtectedApiClientId)) {
        $ProtectedApiClientId = Read-Host "Enter the Protected API app registration Client ID"
    }

    $parsedTenantId = [guid]::Empty
    $parsedApiClientId = [guid]::Empty
    if (-not [guid]::TryParse($TenantId.Trim(), [ref]$parsedTenantId) -or
        -not [guid]::TryParse($ProtectedApiClientId.Trim(), [ref]$parsedApiClientId)) {
        throw "Tenant ID and Protected API Client ID must both be valid GUIDs."
    }

    $jwtSettingsContent = [ordered]@{
        Jwt = [ordered]@{
            Authority = "https://login.microsoftonline.com/$($TenantId.Trim())/v2.0"
            Audiences = @("api://$($ProtectedApiClientId.Trim())")
        }
    } | ConvertTo-Json -Depth 4
}

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

    # Do not remove or recreate bindings here. This preserves HTTPS bindings and certificates.
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

Set-Content -Path $jwtSettingsPath -Value $jwtSettingsContent -Encoding UTF8

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
        $desiredHttpBinding = if ([string]::IsNullOrWhiteSpace($HostName)) {
            "*:${Port}:"
        }
        else {
            "*:${Port}:$HostName"
        }

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
if ([string]::IsNullOrWhiteSpace($HostName)) {
    Write-Host "URL: http://localhost:$Port/"
}
else {
    Write-Host "URL: http://$HostName`:$Port/"
}