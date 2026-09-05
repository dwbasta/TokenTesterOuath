[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath, # Published folder or .zip package

    [string]$SiteName = "OAuthClientCredsTestSite",
    [string]$AppPoolName = "OAuthClientCredsTestSitePool",
    [string]$PhysicalPath = "C:\inetpub\wwwroot\OAuthClientCredsTestSite",
    [int]$Port = 80,
    [string]$HostName = "",
    [switch]$InstallHostingBundle = $true,
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

if ($InstallHostingBundle) {
    Write-Host "==> Installing .NET 8 Hosting Bundle..."
    $installer = Join-Path $env:TEMP "dotnet-hosting-win.exe"
    Invoke-WebRequest -Uri $HostingBundleUrl -OutFile $installer
    Start-Process -FilePath $installer -ArgumentList "/install /quiet /norestart" -Wait
}

Write-Host "==> Preparing website content..."
if (-not (Test-Path $SourcePath)) {
    throw "SourcePath not found: $SourcePath"
}

if (Test-Path $PhysicalPath) {
    Remove-Item "$PhysicalPath\*" -Recurse -Force -ErrorAction SilentlyContinue
}
else {
    New-Item -ItemType Directory -Path $PhysicalPath -Force | Out-Null
}

if ($SourcePath.ToLowerInvariant().EndsWith(".zip")) {
    $extractPath = Join-Path $env:TEMP ("deploy_" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $extractPath -Force | Out-Null
    Expand-Archive -Path $SourcePath -DestinationPath $extractPath -Force

    $rootItems = Get-ChildItem -Path $extractPath
    if ($rootItems.Count -eq 1 -and $rootItems[0].PSIsContainer) {
        Copy-Item -Path (Join-Path $rootItems[0].FullName "*") -Destination $PhysicalPath -Recurse -Force
    }
    else {
        Copy-Item -Path (Join-Path $extractPath "*") -Destination $PhysicalPath -Recurse -Force
    }

    Remove-Item $extractPath -Recurse -Force
}
else {
    Copy-Item -Path (Join-Path $SourcePath "*") -Destination $PhysicalPath -Recurse -Force
}

Write-Host "==> Configuring IIS site..."
Import-Module WebAdministration

if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
    New-WebAppPool -Name $AppPoolName | Out-Null
}
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion -Value ""
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedPipelineMode -Value "Integrated"

if (Test-Path "IIS:\Sites\$SiteName") {
    Stop-Website -Name $SiteName
    Remove-Website -Name $SiteName
}

if ([string]::IsNullOrWhiteSpace($HostName)) {
    New-Website -Name $SiteName -PhysicalPath $PhysicalPath -Port $Port -ApplicationPool $AppPoolName | Out-Null
}
else {
    New-Website -Name $SiteName -PhysicalPath $PhysicalPath -Port $Port -HostHeader $HostName -ApplicationPool $AppPoolName | Out-Null
}

Write-Host "==> Setting folder permissions..."
icacls $PhysicalPath /grant "IIS AppPool\${AppPoolName}:(OI)(CI)(RX)" /T | Out-Null

iisreset | Out-Null

Write-Host ""
Write-Host "Deployment complete."
if ([string]::IsNullOrWhiteSpace($HostName)) {
    Write-Host "URL: http://localhost:$Port/"
}
else {
    Write-Host "URL: http://$HostName`:$Port/"
}