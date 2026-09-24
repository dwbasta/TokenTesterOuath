[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [string]$SiteName = "OUathMCPServer",
    [string]$AppPoolName = "OUathMCPServerPool",
    [string]$PhysicalPath = "C:\inetpub\wwwroot\OUathMCPServer",
    [int]$Port = 80,
    [string]$HostName = "",

    [string]$IdentityProvider = "",
    [string]$TenantId = "",
    [string]$McpServerClientId = "",
    [string]$OktaAuthority = "",
    [string]$OktaMcpAudience = "",
    [string]$OktaValidIssuersCsv = "",
    [string]$RequiredScope = "access_as_user",
    [string]$OboClientSecret = "",
    [string]$DownstreamApiBaseUrl = "",
    [string]$DownstreamScope = "",
    [string]$DownstreamAudience = "",
    [string]$OboTokenEndpoint = "",
    [string]$OboGrantType = "",
    [string]$WriteTokenTraceFile = "",
    [string]$TokenTraceFilePath = "",

    [switch]$OverwriteRuntimeConfig,
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

            Write-Host "File lock encountered; retrying in 3 seconds (attempt $copyAttempt of 5)..." -ForegroundColor Yellow
            Start-Sleep -Seconds 3
        }
    }
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

function Set-ObjectProperty {
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Object,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [object]$Value
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value
        return
    }

    $Object.$Name = $Value
}

function Resolve-BoolSetting {
    param(
        [string]$CurrentValue,
        [object]$ExistingValue,
        [bool]$DefaultValue = $false
    )

    if (-not [string]::IsNullOrWhiteSpace($CurrentValue)) {
        try {
            return [System.Convert]::ToBoolean($CurrentValue.Trim())
        }
        catch {
            throw "Invalid boolean value '$CurrentValue'. Use true or false."
        }
    }

    if ($null -ne $ExistingValue -and -not [string]::IsNullOrWhiteSpace($ExistingValue.ToString())) {
        try {
            return [System.Convert]::ToBoolean($ExistingValue)
        }
        catch {
            return $DefaultValue
        }
    }

    return $DefaultValue
}

if (-not (Test-IsAdmin)) {
    throw "Run this script in an elevated PowerShell session (Run as Administrator)."
}

if (-not (Test-Path $SourcePath)) {
    throw "SourcePath not found: $SourcePath"
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
        throw ".NET 8 Hosting Bundle installation failed with exit code $($process.ExitCode)."
    }

    Remove-Item -Path $installer -Force -ErrorAction SilentlyContinue
}

$existingJwtPath = Join-Path $PhysicalPath "jwtsettings.json"
$existingOboPath = Join-Path $PhysicalPath "obosettings.json"
$existingJwt = $null
$existingObo = $null

if (Test-Path $existingJwtPath) {
    try { $existingJwt = Get-Content -Path $existingJwtPath -Raw | ConvertFrom-Json } catch {}
}
if (Test-Path $existingOboPath) {
    try { $existingObo = Get-Content -Path $existingOboPath -Raw | ConvertFrom-Json } catch {}
}

$existingProvider = ""
$existingAuthority = ""
$existingMcpAudience = ""
$existingValidIssuersCsv = ""

if ($null -ne $existingJwt -and $null -ne $existingJwt.Jwt) {
    if ($null -ne $existingJwt.Jwt.Provider) {
        $existingProvider = $existingJwt.Jwt.Provider.ToString()
    }
    if ($null -ne $existingJwt.Jwt.Authority) {
        $existingAuthority = $existingJwt.Jwt.Authority.ToString()
    }

    $existingAudiences = @($existingJwt.Jwt.Audiences)
    if ($existingAudiences.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($existingAudiences[0])) {
        $existingMcpAudience = $existingAudiences[0].ToString()
    }

    $existingIssuers = @($existingJwt.Jwt.ValidIssuers)
    if ($existingIssuers.Count -gt 0) {
        $existingValidIssuersCsv = ($existingIssuers -join ",")
    }
}

$IdentityProvider = Resolve-IdentityProvider -CurrentProvider $IdentityProvider -ExistingProvider $existingProvider -ExistingAuthority $existingAuthority

$RequiredScope = Resolve-Setting $RequiredScope $existingJwt.Jwt.RequiredScope "Enter required delegated scope"

$resolvedAuthority = ""
$resolvedValidIssuers = @()
$resolvedMcpAudiences = @()
$resolvedMcpClientId = ""
$resolvedOboTokenEndpoint = ""
$resolvedOboGrantType = ""

if ($IdentityProvider -eq "Entra") {
    $TenantId = Resolve-Setting $TenantId "" "Enter the Entra tenant ID"

    if ($TenantId -like "https://login.microsoftonline.com/*/v2.0") {
        $TenantId = ($TenantId -replace "^https://login\.microsoftonline\.com/", "") -replace "/v2\.0$", ""
    }

    if (-not [string]::IsNullOrWhiteSpace($existingMcpAudience)) {
        $existingMcpAudience = $existingMcpAudience -replace "^api://", ""
    }

    $McpServerClientId = Resolve-Setting $McpServerClientId $existingMcpAudience "Enter the MCP Server app registration Client ID"

    $parsedTenantId = [guid]::Empty
    $parsedClientId = [guid]::Empty

    if (-not [guid]::TryParse($TenantId.Trim(), [ref]$parsedTenantId)) {
        throw "Tenant ID must be a valid GUID for Entra."
    }
    if (-not [guid]::TryParse($McpServerClientId.Trim(), [ref]$parsedClientId)) {
        throw "MCP Server Client ID must be a valid GUID for Entra."
    }

    $resolvedAuthority = "https://login.microsoftonline.com/$($TenantId.Trim())/v2.0"
    $resolvedValidIssuers = @(
        "https://login.microsoftonline.com/$($TenantId.Trim())/v2.0",
        "https://sts.windows.net/$($TenantId.Trim())/"
    )
    $resolvedMcpAudiences = @("api://$($McpServerClientId.Trim())")
    $resolvedMcpClientId = $McpServerClientId.Trim()
    $resolvedOboTokenEndpoint = "https://login.microsoftonline.com/$($TenantId.Trim())/oauth2/v2.0/token"
    $resolvedOboGrantType = "urn:ietf:params:oauth:grant-type:jwt-bearer"
}
else {
    $OktaAuthority = Resolve-Setting $OktaAuthority $existingAuthority "Enter Okta authority/issuer URL (example: https://dev-123456.okta.com/oauth2/default)"
    $OktaMcpAudience = Resolve-Setting $OktaMcpAudience $existingMcpAudience "Enter MCP audience (example: api://mcp-server)"
    $McpServerClientId = Resolve-Setting $McpServerClientId $existingObo.Obo.ClientId "Enter MCP confidential client ID"

    if (-not [Uri]::TryCreate($OktaAuthority, [UriKind]::Absolute, [ref]([Uri]$null))) {
        throw "OktaAuthority must be a valid absolute URI."
    }

    $resolvedAuthority = $OktaAuthority.TrimEnd('/')
    $resolvedMcpAudiences = @(Split-CsvValues -Value $OktaMcpAudience)
    if ($resolvedMcpAudiences.Count -eq 0) {
        throw "At least one MCP audience is required for Okta."
    }

    $effectiveIssuersCsv = Resolve-Setting $OktaValidIssuersCsv $existingValidIssuersCsv "Enter valid issuers CSV (leave blank to use authority only)"
    $resolvedValidIssuers = Split-CsvValues -Value $effectiveIssuersCsv
    if ($resolvedValidIssuers.Count -eq 0) {
        $resolvedValidIssuers = @($resolvedAuthority)
    }

    $resolvedMcpClientId = $McpServerClientId.Trim()

    $existingTokenEndpoint = ""
    if ($null -ne $existingObo -and $null -ne $existingObo.Obo -and $null -ne $existingObo.Obo.TokenEndpoint) {
        $existingTokenEndpoint = $existingObo.Obo.TokenEndpoint.ToString()
    }

    $existingGrantType = ""
    if ($null -ne $existingObo -and $null -ne $existingObo.Obo -and $null -ne $existingObo.Obo.GrantType) {
        $existingGrantType = $existingObo.Obo.GrantType.ToString()
    }

    $OboTokenEndpoint = Resolve-Setting $OboTokenEndpoint $existingTokenEndpoint "Enter Okta token endpoint (leave blank to use <authority>/v1/token)"
    if ([string]::IsNullOrWhiteSpace($OboTokenEndpoint)) {
        $OboTokenEndpoint = "$resolvedAuthority/v1/token"
    }

    $OboGrantType = Resolve-Setting $OboGrantType $existingGrantType "Enter OBO grant type (default: urn:ietf:params:oauth:grant-type:token-exchange)"
    if ([string]::IsNullOrWhiteSpace($OboGrantType)) {
        $OboGrantType = "urn:ietf:params:oauth:grant-type:token-exchange"
    }

    $resolvedOboTokenEndpoint = $OboTokenEndpoint.Trim()
    $resolvedOboGrantType = $OboGrantType.Trim()
}

$OboClientSecret = Resolve-Setting $OboClientSecret $existingObo.Obo.ClientSecret "Enter the OBO confidential client secret"
$DownstreamApiBaseUrl = Resolve-Setting $DownstreamApiBaseUrl $existingObo.Obo.DownstreamApiBaseUrl "Enter downstream API base URL (example: https://api.contoso.com/)"
$DownstreamScope = Resolve-Setting $DownstreamScope $existingObo.Obo.DownstreamScope "Enter downstream scope (example Entra: api://target-api-client-id/Api.Read, Okta: api.read)"
$DownstreamAudience = Resolve-Setting $DownstreamAudience $existingObo.Obo.DownstreamAudience "Enter downstream audience (example: api://target-api-client-id)"
$TokenTraceFilePath = Resolve-Setting $TokenTraceFilePath $existingObo.Obo.TokenTraceFilePath "Enter token trace file path (example: logs/obo-token-trace.jsonl)"
$writeTokenTraceFileResolved = Resolve-BoolSetting $WriteTokenTraceFile $existingObo.Obo.WriteTokenTraceFile $false

$parsedDownstreamUri = $null
if (-not [Uri]::TryCreate($DownstreamApiBaseUrl.Trim(), [UriKind]::Absolute, [ref]$parsedDownstreamUri)) {
    throw "DownstreamApiBaseUrl must be a valid absolute URI."
}
if (-not [Uri]::TryCreate($resolvedAuthority, [UriKind]::Absolute, [ref]([Uri]$null))) {
    throw "Resolved JWT authority must be a valid absolute URI."
}
if (-not [Uri]::TryCreate($resolvedOboTokenEndpoint, [UriKind]::Absolute, [ref]([Uri]$null))) {
    throw "OboTokenEndpoint must be a valid absolute URI."
}

Import-Module WebAdministration

if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
    New-WebAppPool -Name $AppPoolName | Out-Null
}
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion -Value ""
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedPipelineMode -Value "Integrated"

$existingSite = Get-Website -Name $SiteName -ErrorAction SilentlyContinue
if ($null -ne $existingSite) {
    if ($existingSite.State -eq "Started") {
        Stop-Website -Name $SiteName
    }

    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $PhysicalPath
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $AppPoolName
}

$appPoolState = (Get-WebAppPoolState -Name $AppPoolName).Value
if ($appPoolState -eq "Started") {
    Stop-WebAppPool -Name $AppPoolName
}

for ($attempt = 0; $attempt -lt 30; $attempt++) {
    if ((Get-WebAppPoolState -Name $AppPoolName).Value -eq "Stopped") {
        break
    }

    Start-Sleep -Seconds 1
}

if ((Get-WebAppPoolState -Name $AppPoolName).Value -ne "Stopped") {
    throw "Application pool '$AppPoolName' did not stop."
}

if (Test-Path $PhysicalPath) {
    if ([string]::IsNullOrWhiteSpace($PhysicalPath) -or $PhysicalPath.Length -lt 10 -or $PhysicalPath -eq "C:\" -or $PhysicalPath -eq "C:\inetpub") {
        throw "PhysicalPath is unsafe. Aborting delete operation."
    }

    Remove-Item "$PhysicalPath\*" -Recurse -Force -ErrorAction SilentlyContinue
}
else {
    New-Item -ItemType Directory -Path $PhysicalPath -Force | Out-Null
}

if ($SourcePath.ToLowerInvariant().EndsWith(".zip")) {
    $extractPath = Join-Path $env:TEMP ("deploy_mcp_" + [guid]::NewGuid().ToString("N"))
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

$jwtSettingsPath = Join-Path $PhysicalPath "jwtsettings.json"
$oboSettingsPath = Join-Path $PhysicalPath "obosettings.json"

$jwtSettingsObject = $null
$oboSettingsObject = $null

if (Test-Path $jwtSettingsPath) {
    try { $jwtSettingsObject = Get-Content -Path $jwtSettingsPath -Raw | ConvertFrom-Json } catch {}
}
if (Test-Path $oboSettingsPath) {
    try { $oboSettingsObject = Get-Content -Path $oboSettingsPath -Raw | ConvertFrom-Json } catch {}
}

if ($null -eq $jwtSettingsObject) {
    $jwtSettingsObject = [pscustomobject]@{}
}
if ($null -eq $jwtSettingsObject.Jwt) {
    $jwtSettingsObject | Add-Member -MemberType NoteProperty -Name Jwt -Value ([pscustomobject]@{})
}

Set-ObjectProperty -Object $jwtSettingsObject.Jwt -Name "Provider" -Value $IdentityProvider
Set-ObjectProperty -Object $jwtSettingsObject.Jwt -Name "Authority" -Value $resolvedAuthority
# When writing JWT settings, force arrays:
Set-ObjectProperty -Object $jwtSettingsObject.Jwt -Name "ValidIssuers" -Value @($resolvedValidIssuers)
Set-ObjectProperty -Object $jwtSettingsObject.Jwt -Name "Audiences" -Value @($resolvedMcpAudiences)
Set-ObjectProperty -Object $jwtSettingsObject.Jwt -Name "RequiredScope" -Value $RequiredScope.Trim()

if ($null -eq $oboSettingsObject) {
    $oboSettingsObject = [pscustomobject]@{}
}
if ($null -eq $oboSettingsObject.Obo) {
    $oboSettingsObject | Add-Member -MemberType NoteProperty -Name Obo -Value ([pscustomobject]@{})
}

Set-ObjectProperty -Object $oboSettingsObject.Obo -Name "ClientId" -Value $resolvedMcpClientId
Set-ObjectProperty -Object $oboSettingsObject.Obo -Name "ClientSecret" -Value $OboClientSecret
Set-ObjectProperty -Object $oboSettingsObject.Obo -Name "DownstreamApiBaseUrl" -Value $DownstreamApiBaseUrl.Trim()
Set-ObjectProperty -Object $oboSettingsObject.Obo -Name "DownstreamScope" -Value $DownstreamScope.Trim()
Set-ObjectProperty -Object $oboSettingsObject.Obo -Name "DownstreamAudience" -Value $DownstreamAudience.Trim()
Set-ObjectProperty -Object $oboSettingsObject.Obo -Name "TokenEndpoint" -Value $resolvedOboTokenEndpoint
Set-ObjectProperty -Object $oboSettingsObject.Obo -Name "GrantType" -Value $resolvedOboGrantType
Set-ObjectProperty -Object $oboSettingsObject.Obo -Name "WriteTokenTraceFile" -Value $writeTokenTraceFileResolved

if (-not [string]::IsNullOrWhiteSpace($TokenTraceFilePath)) {
    Set-ObjectProperty -Object $oboSettingsObject.Obo -Name "TokenTraceFilePath" -Value $TokenTraceFilePath.Trim()
}

# BEFORE clearing/copying PhysicalPath
$existingRuntimeJwtContent = $null
$existingRuntimeOboContent = $null

if (Test-Path $existingJwtPath) {
    $existingRuntimeJwtContent = Get-Content -Path $existingJwtPath -Raw
}
if (Test-Path $existingOboPath) {
    $existingRuntimeOboContent = Get-Content -Path $existingOboPath -Raw
}

if ((Test-Path $jwtSettingsPath) -and -not $OverwriteRuntimeConfig) {
    Write-Host "==> Preserving existing jwtsettings.json (use -OverwriteRuntimeConfig to replace)." -ForegroundColor Yellow
}
elseif (-not $OverwriteRuntimeConfig -and -not [string]::IsNullOrWhiteSpace($existingRuntimeJwtContent)) {
    Set-Content -Path $jwtSettingsPath -Value $existingRuntimeJwtContent -Encoding UTF8
}
else {
    Set-Content -Path $jwtSettingsPath -Value ($jwtSettingsObject | ConvertTo-Json -Depth 10) -Encoding UTF8
}

if ((Test-Path $oboSettingsPath) -and -not $OverwriteRuntimeConfig) {
    Write-Host "==> Preserving existing obosettings.json (use -OverwriteRuntimeConfig to replace)." -ForegroundColor Yellow
}
elseif (-not $OverwriteRuntimeConfig -and -not [string]::IsNullOrWhiteSpace($existingRuntimeOboContent)) {
    Set-Content -Path $oboSettingsPath -Value $existingRuntimeOboContent -Encoding UTF8
}
else {
    Set-Content -Path $oboSettingsPath -Value ($oboSettingsObject | ConvertTo-Json -Depth 10) -Encoding UTF8
}

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
        throw "Cannot create a new IIS site while -SkipHttpBinding is set."
    }

    if ([string]::IsNullOrWhiteSpace($HostName)) {
        New-Website -Name $SiteName -PhysicalPath $PhysicalPath -Port $Port -ApplicationPool $AppPoolName | Out-Null
    }
    else {
        New-Website -Name $SiteName -PhysicalPath $PhysicalPath -Port $Port -HostHeader $HostName -ApplicationPool $AppPoolName | Out-Null
    }
}
elseif (-not $SkipHttpBinding) {
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
}

icacls $PhysicalPath /grant "IIS AppPool\${AppPoolName}:(OI)(CI)(RX)" /T | Out-Null

if ((Get-WebAppPoolState -Name $AppPoolName).Value -ne "Started") {
    Start-WebAppPool -Name $AppPoolName
}
if ((Get-Website -Name $SiteName).State -ne "Started") {
    Start-Website -Name $SiteName
}

Write-Host "Deployment complete."
Write-Host "Identity provider: $IdentityProvider"

