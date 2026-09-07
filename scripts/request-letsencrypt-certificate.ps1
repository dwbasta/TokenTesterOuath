[CmdletBinding()]
param(
	[string]$SiteName = "OAuthClientCredsTestSite",
	[string]$DnsName = "example.contoso.xyz",
	[string]$EmailAddress = "admin@example.com",
	[string]$WacsExe = "C:\Tools\win-acme\wacs.exe"
)

$ErrorActionPreference = "Stop"

Import-Module WebAdministration

if (-not (Test-Path $WacsExe)) {
	throw "wacs.exe was not found: $WacsExe"
}

$site = Get-Website -Name $SiteName -ErrorAction SilentlyContinue
if ($null -eq $site) {
	Write-Host "Available IIS sites:" -ForegroundColor Yellow
	Get-Website | Select-Object Name, ID, State
	throw "IIS site '$SiteName' was not found. Deploy the website first."
}

$bindingExists = Get-WebBinding -Name $SiteName -Protocol http |
	Where-Object { $_.bindingInformation -eq "*:80:$DnsName" }

if ($null -eq $bindingExists) {
	Write-Host "Adding HTTP host-header binding for $DnsName..." -ForegroundColor Cyan
	New-WebBinding `
		-Name $SiteName `
		-Protocol http `
		-Port 80 `
		-IPAddress "*" `
		-HostHeader $DnsName | Out-Null
}

Write-Host "Current bindings:" -ForegroundColor Green
Get-WebBinding -Name $SiteName |
	Select-Object Protocol, BindingInformation

Write-Host "Launching win-acme for IIS site ID $($site.Id)..." -ForegroundColor Cyan
& $WacsExe `
	--target iis `
	--siteid $site.Id `
	--installation iis `
	--emailaddress $EmailAddress `
	--accepttos `
	--closeonfinish

if ($LASTEXITCODE -ne 0) {
	throw "win-acme failed with exit code $LASTEXITCODE."
}

Write-Host "HTTPS bindings:" -ForegroundColor Green
Get-WebBinding -Name $SiteName -Protocol https |
	Select-Object Protocol, BindingInformation

Write-Host "Recent certificates:" -ForegroundColor Green
Get-ChildItem Cert:\LocalMachine\My |
	Sort-Object NotAfter -Descending |
	Select-Object Subject, NotAfter -First 5
