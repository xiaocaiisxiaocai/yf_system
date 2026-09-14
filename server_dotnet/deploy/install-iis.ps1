#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
Install the ASP.NET Core + React package as a new HTTPS IIS site on the target server.
.DESCRIPTION
Fresh-site installation only. Existing IIS sites, pools and nonempty destination folders
are refused. Database initialization is a separate explicit command; no data is migrated.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]*$')][string]$HostName,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Fa-f0-9 ]+$')][string]$CertificateThumbprint,
    [Parameter(Mandatory=$true)][string]$ConfigPath,
    [string]$PackageRoot = $PSScriptRoot,
    [string]$SiteName = 'YfSystemDotNet',
    [string]$AppPoolName = 'YfSystemDotNet',
    [string]$SiteRoot = 'C:\inetpub\yf_system_dotnet',
    [ValidateRange(1,65535)][int]$HttpsPort = 443
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function FullPath([string]$Value) {
    if ($Value -notmatch '^[A-Za-z]:[\\/]') { throw 'Use absolute local disk paths.' }
    $resolved = [IO.Path]::GetFullPath($Value).TrimEnd('\','/')
    if ($resolved.Length -le 3) { throw 'A drive root is not allowed.' }
    return $resolved
}
function Within([string]$Child,[string]$Parent) {
    return $Child.Equals($Parent,[StringComparison]::OrdinalIgnoreCase) -or $Child.StartsWith($Parent+'\',[StringComparison]::OrdinalIgnoreCase)
}
function NoLinks([string]$Value) {
    $cursor = $Value
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Reparse points are not allowed: $Value" }
        $cursor = Split-Path -Parent $cursor
    }
    if (Test-Path -LiteralPath $Value -PathType Container) {
        if (@(Get-ChildItem -LiteralPath $Value -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw "Reparse point inside: $Value" }
    }
}
if ($SiteName -notmatch '^[A-Za-z0-9_-]+$' -or $AppPoolName -notmatch '^[A-Za-z0-9_-]+$') { throw 'Use letters, numbers, underscore or hyphen in site and pool names.' }
$PackageRoot = FullPath $PackageRoot
$SiteRoot = FullPath $SiteRoot
$ConfigPath = FullPath $ConfigPath
foreach ($path in @($PackageRoot,$SiteRoot,$ConfigPath)) { NoLinks $path }
if (!(Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { throw 'External configuration does not exist.' }
if ((Test-Path -LiteralPath $SiteRoot) -and @(Get-ChildItem -LiteralPath $SiteRoot -Force).Count) { throw 'Destination must be absent or empty; existing deployment is not overwritten.' }
$config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$storage = FullPath $config.App.StorageRoot
NoLinks $storage
if (!(Test-Path -LiteralPath $storage -PathType Container)) { throw 'Create the independent storage directory before installation.' }
foreach ($other in @($PackageRoot,$SiteRoot,$ConfigPath)) {
    if ((Within $storage $other) -or (Within $other $storage)) { throw 'Package, destination, external configuration and storage must be separate.' }
}
$origin = 'https://' + $HostName + $(if ($HttpsPort -eq 443) { '' } else { ':'+$HttpsPort })
if ($config.App.CookieSecure -ne $true -or $config.App.WebBaseUrl.TrimEnd('/') -ne $origin) { throw 'Production configuration requires CookieSecure=true and WebBaseUrl equal to the HTTPS site origin.' }
if ([string]::IsNullOrWhiteSpace($config.App.ConnectionString) -or [Text.Encoding]::UTF8.GetByteCount($config.App.JwtSecret) -lt 32) { throw 'Database connection and a random JWT secret (at least 32 bytes) are required.' }
$manifestPath = Join-Path $PackageRoot 'manifest.json'
if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Package manifest missing.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($entry in $manifest.files) {
    $file = [IO.Path]::GetFullPath((Join-Path $PackageRoot $entry.path))
    if (!(Within $file $PackageRoot) -or !(Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Invalid manifest path or missing file.' }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Package hash mismatch: $($entry.path)" }
}
$knownPaths = @($manifest.files | ForEach-Object { $_.path.Replace('/','\').ToLowerInvariant() })
foreach ($actual in Get-ChildItem -LiteralPath $PackageRoot -Recurse -File -Force) {
    $relative = $actual.FullName.Substring($PackageRoot.Length).TrimStart('\').ToLowerInvariant()
    if ($relative -ne 'manifest.json' -and $knownPaths -notcontains $relative) { throw "Unlisted file in package: $relative" }
}
foreach ($required in @('Yf.Api.dll','Yf.Api.runtimeconfig.json','web.config','wwwroot\index.html','maintenance-common.ps1')) {
    if (!(Test-Path -LiteralPath (Join-Path $PackageRoot $required) -PathType Leaf)) { throw "Missing required payload: $required" }
}
. (Join-Path $PackageRoot 'maintenance-common.ps1')
Assert-YfSeparate @($PackageRoot,$SiteRoot,$ConfigPath,$storage)
$maintenanceConfig = Read-YfMaintenanceConfig $ConfigPath
if ($maintenanceConfig.Storage -ne $storage) { throw 'Configuration storage path changed during validation.' }
Assert-YfPublishedConfig $PackageRoot
Import-Module WebAdministration -ErrorAction Stop
if (Test-Path "IIS:\Sites\$SiteName") { throw 'IIS site already exists; follow documented upgrade procedure.' }
if (Test-Path "IIS:\AppPools\$AppPoolName") { throw 'IIS application pool already exists; refusing to reuse another application pool.' }
if (!(Get-WebGlobalModule | Where-Object Name -eq 'AspNetCoreModuleV2')) { throw 'Install the .NET 10 Hosting Bundle after IIS, then restart IIS/WAS as its installer directs.' }
if (!(Get-WebGlobalModule | Where-Object Name -eq 'ApplicationInitializationModule')) { throw 'Enable IIS Application Initialization before installation.' }
Assert-YfInstallationConfiguration $SiteName
$runtimes = & dotnet --list-runtimes
if ($LASTEXITCODE -ne 0 -or !($runtimes -match '^Microsoft.AspNetCore.App 10\.')) { throw '.NET 10 ASP.NET Core runtime is required.' }
$thumb = $CertificateThumbprint.Replace(' ','').ToUpperInvariant()
$certificate = Get-Item "Cert:\LocalMachine\My\$thumb" -ErrorAction Stop
if (!$certificate.HasPrivateKey -or $certificate.NotAfter -lt (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) { throw 'A currently valid LocalMachine/My HTTPS certificate with private key is required.' }
$bindingInfo = '*:' + $HttpsPort + ':' + $HostName
if (Get-WebBinding | Where-Object { $_.bindingInformation -eq $bindingInfo }) { throw 'The requested HTTPS binding is already in use.' }

New-Item -ItemType Directory -Path $SiteRoot -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $PackageRoot -Force) {
    if ($item.Name -in @('install-iis.ps1','README.md','appsettings.example.json','manifest.json')) { continue }
    Copy-Item -LiteralPath $item.FullName -Destination $SiteRoot -Recurse
}
$webConfigPath = Join-Path $SiteRoot 'web.config'
[xml]$webConfig = Get-Content -LiteralPath $webConfigPath -Raw -Encoding UTF8
$asp = $webConfig.SelectSingleNode('//aspNetCore')
if (!$asp) { throw 'Published ASP.NET Core IIS configuration missing.' }
$environment = $asp.SelectSingleNode('environmentVariables')
if (!$environment) { $environment = $webConfig.CreateElement('environmentVariables'); $asp.AppendChild($environment) | Out-Null }
foreach ($pair in @(@('YF_CONFIG_PATH',$ConfigPath),@('ASPNETCORE_ENVIRONMENT','Production'))) {
    $node = $webConfig.CreateElement('environmentVariable'); $node.SetAttribute('name',$pair[0]); $node.SetAttribute('value',$pair[1]); $environment.AppendChild($node) | Out-Null
}
$webConfig.Save($webConfigPath)
New-WebAppPool -Name $AppPoolName | Out-Null
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion -Value ''
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType -Value 'ApplicationPoolIdentity'
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.loadUserProfile -Value $false
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name startMode -Value AlwaysRunning
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name enable32BitAppOnWin64 -Value $false
$identity = 'IIS AppPool\' + $AppPoolName
& icacls.exe $SiteRoot /grant "${identity}:(OI)(CI)RX" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Unable to grant application read permissions.' }
& icacls.exe $storage /grant "${identity}:(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Unable to grant storage permissions.' }
Protect-YfConfigurationFile $ConfigPath $identity
New-Website -Name $SiteName -PhysicalPath $SiteRoot -ApplicationPool $AppPoolName -Port $HttpsPort -HostHeader $HostName -Ssl -SslFlags 1 | Out-Null
# Browser SignalR handshakes carry a short-lived access_token in the query string.
# Keep request metadata while excluding URI Query from this application's IIS log.
Set-ItemProperty "IIS:\Sites\$SiteName" -Name logFile.logExtFileFlags -Value 'Date,Time,ClientIP,UserName,SiteName,ServerIP,Method,UriStem,ServerPort,UserAgent,HttpStatus,HttpSubStatus,Win32Status,TimeTaken'
$binding = Get-WebBinding -Name $SiteName -Protocol https
$binding.AddSslCertificate($thumb,'My')
Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationDefaults.preloadEnabled -Value $true
Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Filter 'system.webServer/security/requestFiltering/requestLimits' -Name maxAllowedContentLength -Value 67108864
Start-WebAppPool -Name $AppPoolName
Start-Website -Name $SiteName
Write-Host "Installed new site: $origin"
Write-Host "Check $origin/health and browser login from the target network. Database was not initialized or migrated by this installer."
