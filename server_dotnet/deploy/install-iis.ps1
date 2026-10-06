#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
Install the ASP.NET Core + React package as a new HTTPS IIS site on the target server.
.DESCRIPTION
Fresh-site installation only. Existing IIS sites, pools and nonempty destination folders
are refused. No data is migrated. With App.AutoInitializeDatabase=true the application creates
and seeds an absent or empty database on first start and the installer then requires /health
to report status=ok, db=up (otherwise it rolls back); without it, initialization is a separate
explicit command and /health must be checked manually afterwards.
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
    [ValidateRange(1,65535)][int]$HttpsPort = 443,
    # Serilog file directory. Defaults to App.LogDirectory from -ConfigPath (the same way StorageRoot
    # comes from the configuration); when both are given they must name the same directory.
    [string]$LogRoot,
    # After starting the site, /health must report status=ok, db=up or the installation is rolled back.
    # Only skip when the host name cannot be reached from this server (for example DNS not switched yet).
    [switch]$SkipHealthCheck,
    [ValidateRange(30,600)][int]$HealthCheckWaitSeconds = 180,
    [ValidateRange(2,30)][int]$HealthRequestTimeoutSeconds = 10
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
$oemStorage = $null
if ($config.App.PSObject.Properties['OemStorageRoot'] -and ![string]::IsNullOrWhiteSpace([string]$config.App.OemStorageRoot)) {
    $oemStorage = FullPath ([string]$config.App.OemStorageRoot)
    NoLinks $oemStorage
    if (!(Test-Path -LiteralPath $oemStorage -PathType Container)) { throw 'Create the independent OEM storage directory before installation.' }
    foreach ($other in @($PackageRoot,$SiteRoot,$ConfigPath,$storage)) {
        if ((Within $oemStorage $other) -or (Within $other $oemStorage)) { throw 'OEM storage must be separate from package, destination, configuration and collaboration storage.' }
    }
}
$configuredLogRoot = if ($config.App.PSObject.Properties['LogDirectory'] -and ![string]::IsNullOrWhiteSpace([string]$config.App.LogDirectory)) { FullPath $config.App.LogDirectory } else { '' }
if ($LogRoot) {
    $LogRoot = FullPath $LogRoot
    if (!$configuredLogRoot) { throw 'Add the same path as App.LogDirectory to the external configuration; the installer does not rewrite it.' }
    if (!$LogRoot.Equals($configuredLogRoot,[StringComparison]::OrdinalIgnoreCase)) { throw '-LogRoot differs from App.LogDirectory in the external configuration.' }
} else {
    $LogRoot = $configuredLogRoot
}
if ($LogRoot) {
    NoLinks $LogRoot
    foreach ($other in @($PackageRoot,$SiteRoot,$ConfigPath,$storage,$oemStorage) | Where-Object { $_ }) {
        if ((Within $LogRoot $other) -or (Within $other $LogRoot)) { throw 'The log directory must be separate from package, destination, configuration and storage.' }
    }
    if ((Test-Path -LiteralPath $LogRoot) -and !(Test-Path -LiteralPath $LogRoot -PathType Container)) { throw 'The log directory path is a file.' }
}
$origin = 'https://' + $HostName + $(if ($HttpsPort -eq 443) { '' } else { ':'+$HttpsPort })
if ($config.App.CookieSecure -ne $true -or $config.App.WebBaseUrl.TrimEnd('/') -ne $origin) { throw 'Production configuration requires CookieSecure=true and WebBaseUrl equal to the HTTPS site origin.' }
if ($config.App.PSObject.Properties['AllowInsecureCookies'] -and $config.App.AllowInsecureCookies -eq $true) { throw 'Production HTTPS configuration must not set App.AllowInsecureCookies=true.' }
if ([string]::IsNullOrWhiteSpace($config.App.ConnectionString) -or [Text.Encoding]::UTF8.GetByteCount($config.App.JwtSecret) -lt 32) { throw 'Database connection and a random JWT secret (at least 32 bytes) are required.' }
$manifestPath = Join-Path $PackageRoot 'manifest.json'
if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Package manifest missing.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) { throw 'Unsupported package manifest schema version.' }
$knownPaths = @{}
foreach ($entry in $manifest.files) {
    $file = [IO.Path]::GetFullPath((Join-Path $PackageRoot $entry.path))
    $relative = $file.Substring($PackageRoot.Length).TrimStart('\').ToLowerInvariant()
    if (!(Within $file $PackageRoot) -or $knownPaths.ContainsKey($relative) -or !(Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Invalid, duplicate or missing manifest file.' }
    if ((Get-Item -LiteralPath $file -Force).Length -ne $entry.bytes) { throw "Package length mismatch: $($entry.path)" }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Package hash mismatch: $($entry.path)" }
    $knownPaths[$relative] = $true
}
foreach ($actual in Get-ChildItem -LiteralPath $PackageRoot -Recurse -File -Force) {
    $relative = $actual.FullName.Substring($PackageRoot.Length).TrimStart('\').ToLowerInvariant()
    if ($relative -ne 'manifest.json' -and !$knownPaths.ContainsKey($relative)) { throw "Unlisted file in package: $relative" }
}
foreach ($required in @('Yf.Api.dll','Yf.Api.runtimeconfig.json','web.config','wwwroot\index.html','maintenance-common.ps1')) {
    if (!(Test-Path -LiteralPath (Join-Path $PackageRoot $required) -PathType Leaf)) { throw "Missing required payload: $required" }
}
. (Join-Path $PackageRoot 'maintenance-common.ps1')
Assert-YfManifest $PackageRoot | Out-Null
Assert-YfSeparate @($PackageRoot,$SiteRoot,$ConfigPath,$storage)
if ($oemStorage) { Assert-YfSeparate @($PackageRoot,$SiteRoot,$ConfigPath,$storage,$oemStorage) }
if ($LogRoot) {
    $separatePaths = @($PackageRoot,$SiteRoot,$ConfigPath,$storage,$oemStorage,$LogRoot) | Where-Object { $_ }
    Assert-YfSeparate $separatePaths
}
$maintenanceConfig = Read-YfMaintenanceConfig $ConfigPath
if ($maintenanceConfig.Storage -ne $storage) { throw 'Configuration storage path changed during validation.' }
if ($maintenanceConfig.OemStorage -ne $oemStorage) { throw 'Configuration OEM storage path changed during validation.' }
Assert-YfPublishedConfig $PackageRoot
Import-Module WebAdministration -ErrorAction Stop
if (Test-Path "IIS:\Sites\$SiteName") { throw 'IIS site already exists; follow documented upgrade procedure.' }
if (Test-Path "IIS:\AppPools\$AppPoolName") { throw 'IIS application pool already exists; refusing to reuse another application pool.' }
if (!(Get-WebGlobalModule | Where-Object Name -eq 'AspNetCoreModuleV2')) { throw 'Install the .NET 8 Hosting Bundle after IIS, then restart IIS/WAS as its installer directs.' }
if (!(Get-WebGlobalModule | Where-Object Name -eq 'ApplicationInitializationModule')) { throw 'Enable IIS Application Initialization before installation.' }
Assert-YfInstallationConfiguration $SiteName
$runtimes = & dotnet --list-runtimes
if ($LASTEXITCODE -ne 0 -or !($runtimes -match '^Microsoft\.NETCore\.App 8\.') -or
    !($runtimes -match '^Microsoft\.AspNetCore\.App 8\.')) {
    throw '.NET 8 ASP.NET Core and .NET runtimes are required.'
}
$thumb = $CertificateThumbprint.Replace(' ','').ToUpperInvariant()
$certificate = Get-Item "Cert:\LocalMachine\My\$thumb" -ErrorAction Stop
if (!$certificate.HasPrivateKey -or $certificate.NotAfter -lt (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) { throw 'A currently valid LocalMachine/My HTTPS certificate with private key is required.' }
$bindingInfo = '*:' + $HttpsPort + ':' + $HostName
if (Get-WebBinding | Where-Object { $_.bindingInformation -eq $bindingInfo }) { throw 'The requested HTTPS binding is already in use.' }

$siteRootExisted = Test-Path -LiteralPath $SiteRoot -PathType Container
$siteRootAcl = if ($siteRootExisted) { Get-Acl -LiteralPath $SiteRoot } else { $null }
$storageAcl = Get-Acl -LiteralPath $storage
$oemStorageAcl = if ($oemStorage) { Get-Acl -LiteralPath $oemStorage } else { $null }
$logRootExisted = $LogRoot -and (Test-Path -LiteralPath $LogRoot -PathType Container)
$logRootAcl = if ($logRootExisted) { Get-Acl -LiteralPath $LogRoot } else { $null }
$createdLogRoot = $false
$configAcl = Get-Acl -LiteralPath $ConfigPath
$copiedTargets = @()
$createdPool = $false
$createdSite = $false
try {
    New-Item -ItemType Directory -Path $SiteRoot -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $PackageRoot -Force) {
        if ($item.Name -in @('install-iis.ps1','README.md','appsettings.example.json','manifest.json')) { continue }
        $destination = Join-Path $SiteRoot $item.Name
        $copiedTargets += $destination
        Copy-Item -LiteralPath $item.FullName -Destination $destination -Recurse
    }
    Set-YfExternalConfigurationFallback $SiteRoot
    Set-YfWebConfigEnvironment (Join-Path $SiteRoot 'web.config') $ConfigPath
    # Realtime collaboration prefers WebSockets. Without the IIS WebSocket Protocol role service
    # (Windows Server feature Web-WebSockets) SignalR falls back to slower transports or polling.
    if (!(Get-WebGlobalModule -Name 'WebSocketModule' -ErrorAction SilentlyContinue)) {
        Write-Warning 'IIS WebSocket Protocol is not installed (Install-WindowsFeature Web-WebSockets). Realtime updates will fall back to slower transports.'
    }
    New-WebAppPool -Name $AppPoolName | Out-Null
    $createdPool = $true
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion -Value ''
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType -Value 'ApplicationPoolIdentity'
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.loadUserProfile -Value $false
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.maxProcesses -Value 1
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name startMode -Value AlwaysRunning
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name enable32BitAppOnWin64 -Value $false
    $installedPool = Get-Item "IIS:\AppPools\$AppPoolName"
    Assert-YfApplicationPoolProcessModel ($installedPool.processModel.identityType.ToString()) ([bool]$installedPool.processModel.loadUserProfile) ([int]$installedPool.processModel.maxProcesses)
    $identity = 'IIS AppPool\' + $AppPoolName
    & icacls.exe $SiteRoot /grant "${identity}:(OI)(CI)RX" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to grant application read permissions.' }
    # Business storage: inheritance off; SYSTEM and Administrators FullControl, pool identity Modify.
    Set-YfApplicationDirectoryAcl $storage $identity
    if ($oemStorage) { Set-YfApplicationDirectoryAcl $oemStorage $identity }
    if ($LogRoot) {
        if (!$logRootExisted) { New-Item -ItemType Directory -Path $LogRoot | Out-Null; $createdLogRoot = $true }
        Set-YfApplicationDirectoryAcl $LogRoot $identity
    }
    Protect-YfConfigurationFile $ConfigPath $identity
    New-Website -Name $SiteName -PhysicalPath $SiteRoot -ApplicationPool $AppPoolName -Port $HttpsPort -HostHeader $HostName -Ssl -SslFlags 1 | Out-Null
    $createdSite = $true
    # Browser SignalR handshakes carry a short-lived access_token in the query string.
    # Keep request metadata while excluding URI Query from this application's IIS log.
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name logFile.logExtFileFlags -Value 'Date,Time,ClientIP,UserName,SiteName,ServerIP,Method,UriStem,ServerPort,UserAgent,HttpStatus,HttpSubStatus,Win32Status,TimeTaken'
    $binding = Get-WebBinding -Name $SiteName -Protocol https
    $binding.AddSslCertificate($thumb,'My')
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationDefaults.preloadEnabled -Value $true
    Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Filter 'system.webServer/security/requestFiltering/requestLimits' -Name maxAllowedContentLength -Value 67108864
    Start-WebAppPool -Name $AppPoolName
    Start-Website -Name $SiteName
    # An app-level startup failure (HTTP 500.30, bad configuration, unreachable database) is only
    # visible over HTTP. With AutoInitializeDatabase=false the empty database is initialized later
    # by an explicit command, so the application cannot report healthy yet and the probe is skipped.
    if ($SkipHealthCheck) {
        Write-Warning "Health check skipped by request. Check $origin/health manually before opening the site."
    } elseif (!$config.App.PSObject.Properties['AutoInitializeDatabase'] -or $config.App.AutoInitializeDatabase -ne $true) {
        Write-Warning "AutoInitializeDatabase is off: initialize the database explicitly, then check $origin/health manually."
    } else {
        Wait-YfHealth $origin $HealthCheckWaitSeconds $HealthRequestTimeoutSeconds
        Write-Host "Health check passed: $origin/health reports status=ok, db=up."
    }
    Write-Host "Installed new site: $origin"
    Write-Host "Check browser login from the target network. Existing databases were not migrated by this installer."
} catch {
    $originalError = $_
    $cleanupErrors = @()
    if ($createdSite -or (Test-Path "IIS:\Sites\$SiteName")) {
        try { Remove-Website -Name $SiteName } catch { $cleanupErrors += 'IIS site' }
    }
    if ($createdPool -or (Test-Path "IIS:\AppPools\$AppPoolName")) {
        try {
            if ((Get-WebAppPoolState -Name $AppPoolName -ErrorAction SilentlyContinue).Value -eq 'Started') { Stop-WebAppPool -Name $AppPoolName }
            Remove-WebAppPool -Name $AppPoolName
        } catch { $cleanupErrors += 'application pool' }
    }
    try { Set-Acl -LiteralPath $storage -AclObject $storageAcl } catch { $cleanupErrors += 'storage ACL' }
    if ($oemStorageAcl) {
        try { Set-Acl -LiteralPath $oemStorage -AclObject $oemStorageAcl } catch { $cleanupErrors += 'OEM storage ACL' }
    }
    if ($createdLogRoot) {
        # Remove only an empty directory this run created; log files written by a failed start are kept.
        try { if (!@(Get-ChildItem -LiteralPath $LogRoot -Force).Count) { Remove-Item -LiteralPath $LogRoot } } catch { $cleanupErrors += 'log directory' }
    } elseif ($logRootAcl) {
        try { Set-Acl -LiteralPath $LogRoot -AclObject $logRootAcl } catch { $cleanupErrors += 'log directory ACL' }
    }
    try { Set-Acl -LiteralPath $ConfigPath -AclObject $configAcl } catch { $cleanupErrors += 'configuration ACL' }
    foreach ($target in $copiedTargets) {
        try { if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force } } catch { $cleanupErrors += $target }
    }
    if ($siteRootExisted) {
        try { Set-Acl -LiteralPath $SiteRoot -AclObject $siteRootAcl } catch { $cleanupErrors += 'site root ACL' }
    } else {
        try { if (Test-Path -LiteralPath $SiteRoot) { Remove-Item -LiteralPath $SiteRoot -Force } } catch { $cleanupErrors += 'site root' }
    }
    if ($cleanupErrors.Count) { Write-Warning ('Installation failed and rollback was incomplete: ' + ($cleanupErrors -join ', ')) }
    throw $originalError
}
