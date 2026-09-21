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
    [string]$ClamAvInstallRoot = 'C:\Program Files\YfSystem\ClamAV',
    [string]$ClamAvDataRoot = 'C:\ProgramData\YfSystem\ClamAV',
    [switch]$UseExistingClamAv,
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
function Read-ClamAvConfig([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "ClamAV configuration is missing: $Path" }
    $result = @{}
    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        $trimmed = $line.Trim()
        if (!$trimmed -or $trimmed.StartsWith('#')) { continue }
        if ($trimmed -notmatch '^(\S+)\s+(.+)$') { throw "Invalid ClamAV configuration line in $Path." }
        if ($result.ContainsKey($matches[1])) { throw "Duplicate ClamAV directive $($matches[1]) in $Path." }
        $result[$matches[1]] = $matches[2].Trim().Trim('"')
    }
    return $result
}
function Invoke-ClamAvCommand([string]$Address,[int]$TcpPort,[string]$Command) {
    $client = New-Object Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect($Address,$TcpPort,$null,$null)
        if (!$async.AsyncWaitHandle.WaitOne(2000)) { throw 'ClamD connection timed out.' }
        $client.EndConnect($async)
        $stream = $client.GetStream(); $stream.ReadTimeout = 3000
        $request = [Text.Encoding]::ASCII.GetBytes(('z' + $Command + "`0"))
        $stream.Write($request,0,$request.Length)
        $buffer = New-Object byte[] 1024
        $read = $stream.Read($buffer,0,$buffer.Length)
        return [Text.Encoding]::ASCII.GetString($buffer,0,$read).Trim([char]0)
    }
    finally { $client.Dispose() }
}
function Assert-ClamAvAcl([string[]]$Paths) {
    $allowedSids = @('S-1-5-18','S-1-5-32-544')
    foreach ($path in $Paths) {
        $acl = Get-Acl -LiteralPath $path
        foreach ($rule in $acl.Access) {
            if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { continue }
            try { $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value }
            catch { throw "Unable to resolve ACL identity on trusted ClamAV path: $path" }
            if ($allowedSids -notcontains $sid) { throw "Trusted ClamAV path grants access to an unexpected identity: $path ($sid)" }
        }
    }
    foreach ($root in @($Paths[0],$Paths[1])) {
        if (!(Get-Acl -LiteralPath $root).AreAccessRulesProtected) { throw "Trusted ClamAV root still inherits ACL entries: $root" }
    }
}
function Assert-ExistingClamAv([string]$InstallRoot,[string]$DataRoot) {
    if (!(Test-Path -LiteralPath $InstallRoot -PathType Container) -or !(Test-Path -LiteralPath $DataRoot -PathType Container)) {
        throw 'UseExistingClamAv requires the requested ClamAV program and data directories.'
    }
    $clamdExe = @(Get-ChildItem -LiteralPath $InstallRoot -Recurse -File -Filter 'clamd.exe')
    $freshclamExe = @(Get-ChildItem -LiteralPath $InstallRoot -Recurse -File -Filter 'freshclam.exe')
    if ($clamdExe.Count -ne 1 -or $freshclamExe.Count -ne 1 -or $clamdExe[0].DirectoryName -ne $freshclamExe[0].DirectoryName) {
        throw 'UseExistingClamAv requires exactly one co-located clamd.exe and freshclam.exe.'
    }
    $expectedClamdSha256 = '540f4aabb63a42c59cbb154e5de2ef039854587b953525aac2a55fdca33340f9'
    $expectedFreshclamSha256 = 'f6ab286032b04ccb0996206182e019566bd818bdb8e724aeac522d5f3307c758'
    if ((Get-FileHash -LiteralPath $clamdExe[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedClamdSha256 -or
        (Get-FileHash -LiteralPath $freshclamExe[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedFreshclamSha256) {
        throw 'Existing ClamAV executables do not match the pinned official 1.4.6 Windows x64 distribution.'
    }
    $clamdConfigPath = Join-Path $clamdExe[0].DirectoryName 'clamd.conf'
    $freshclamConfigPath = Join-Path $freshclamExe[0].DirectoryName 'freshclam.conf'
    $clamdConfig = Read-ClamAvConfig $clamdConfigPath
    $freshclamConfig = Read-ClamAvConfig $freshclamConfigPath
    $expectedClamd = @{
        TCPAddr='127.0.0.1'; TCPSocket='3310'; StreamMaxLength='1G'; MaxScanSize='1536M';
        MaxFileSize='1G'; MaxFiles='10000'; MaxRecursion='16'; PCREMaxFileSize='1G';
        AlertExceedsMax='yes'; AlertEncrypted='yes'
    }
    foreach ($name in $expectedClamd.Keys) {
        if (!$clamdConfig.ContainsKey($name) -or $clamdConfig[$name] -cne $expectedClamd[$name]) {
            throw "Existing ClamD configuration does not match required directive $name."
        }
    }
    $expectedDatabase = Join-Path $DataRoot 'database'
    $expectedLog = Join-Path $DataRoot 'logs\clamd.log'
    $expectedTemp = Join-Path $DataRoot 'temp'
    foreach ($pair in @(@('DatabaseDirectory',$expectedDatabase),@('LogFile',$expectedLog),@('TemporaryDirectory',$expectedTemp))) {
        if (!$clamdConfig.ContainsKey($pair[0]) -or (FullPath $clamdConfig[$pair[0]]) -ne (FullPath $pair[1])) {
            throw "Existing ClamD configuration points $($pair[0]) outside the requested data root."
        }
    }
    if (!$freshclamConfig.ContainsKey('DatabaseDirectory') -or
        (FullPath $freshclamConfig['DatabaseDirectory']) -ne (FullPath $expectedDatabase) -or
        !$freshclamConfig.ContainsKey('NotifyClamd') -or
        (FullPath $freshclamConfig['NotifyClamd']) -ne (FullPath $clamdConfigPath) -or
        !$freshclamConfig.ContainsKey('Checks') -or $freshclamConfig['Checks'] -ne '12') {
        throw 'Existing FreshClam configuration does not match the requested database, ClamD config and update schedule.'
    }
    Assert-ClamAvAcl @($InstallRoot,$DataRoot,$clamdExe[0].DirectoryName,$expectedDatabase,$clamdExe[0].FullName,$freshclamExe[0].FullName,$clamdConfigPath,$freshclamConfigPath)
    foreach ($databaseName in @('main','daily','bytecode')) {
        $database = @(Get-ChildItem -LiteralPath $expectedDatabase -File -ErrorAction Stop | Where-Object { $_.BaseName -eq $databaseName -and $_.Extension -in @('.cvd','.cld') })
        if ($database.Count -ne 1) { throw "Existing ClamAV data lacks exactly one $databaseName database." }
    }
    $clamdService = Get-CimInstance Win32_Service -Filter "Name='clamd'" -ErrorAction Stop
    $freshclamService = Get-CimInstance Win32_Service -Filter "Name='freshclam'" -ErrorAction Stop
    if (!$clamdService -or !$freshclamService) { throw 'Existing clamd and freshclam services are both required.' }
    $expectedClamdPath = '"' + $clamdExe[0].FullName + '" --daemon --service-mode --config-file="' + $clamdConfigPath + '"'
    $expectedFreshclamPath = '"' + $freshclamExe[0].FullName + '" --daemon --service-mode --config-file="' + $freshclamConfigPath + '"'
    if ($clamdService.PathName -ine $expectedClamdPath -or $freshclamService.PathName -ine $expectedFreshclamPath) {
        throw 'Existing ClamAV service command lines do not point to the requested trusted program and configuration.'
    }
    if ($clamdService.State -ne 'Running' -or [int]$clamdService.ProcessId -le 0 -or
        $freshclamService.State -notin @('Running','Stopped') -or
        ($freshclamService.State -eq 'Running' -and [int]$freshclamService.ProcessId -le 0)) {
        throw 'Existing ClamD must be running; its verified FreshClam service must be running or stopped.'
    }
    $listeners = @(Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort 3310 -State Listen -ErrorAction Stop |
        Where-Object { $_.OwningProcess -eq [int]$clamdService.ProcessId })
    if ($listeners.Count -ne 1) { throw 'The 127.0.0.1:3310 listener is not owned by the existing clamd service process.' }
    if ((Invoke-ClamAvCommand '127.0.0.1' 3310 'PING') -ne 'PONG') { throw 'Existing ClamD did not answer PING.' }
    $version = Invoke-ClamAvCommand '127.0.0.1' 3310 'VERSION'
    if ($version -notmatch '^ClamAV 1\.4\.6/[1-9][0-9]*/.+$') { throw 'Existing ClamD VERSION does not prove ClamAV 1.4.6 with a loaded database.' }
    Write-Host "Using verified existing ClamAV services: $version"
    if ($freshclamService.State -eq 'Stopped') {
        Write-Warning 'FreshClam is stopped; the signed installed snapshot remains usable subject to the application freshness policy. Run update-clamav.ps1 after connectivity is restored.'
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
# OEM file storage: optional, local, never backed up, separate from everything else.
$oemStorage = $null
$oemProperty = $config.App.PSObject.Properties['OemStorageRoot']
if ($oemProperty -and ![string]::IsNullOrWhiteSpace($oemProperty.Value)) {
    $oemStorage = FullPath $oemProperty.Value
    NoLinks $oemStorage
    if (!(Test-Path -LiteralPath $oemStorage -PathType Container)) { throw 'Create the OEM storage directory (App:OemStorageRoot) before installation.' }
    foreach ($other in @($PackageRoot,$SiteRoot,$ConfigPath,$storage)) {
        if ((Within $oemStorage $other) -or (Within $other $oemStorage)) { throw 'OEM storage must be separate from the package, site, configuration and backed-up storage.' }
    }
}
$oemScanner = $config.App.PSObject.Properties['OemScanner']
if ($oemScanner -and $oemScanner.Value.PSObject.Properties['Engine'] -and $oemScanner.Value.Engine -eq 'Fake') { throw 'The Fake OEM scanner is for development only and cannot be installed in production.' }
if ($oemScanner -and $oemScanner.Value.PSObject.Properties['Engine'] -and $oemScanner.Value.Engine -eq 'OnAccess') {
    if (!$oemStorage) { throw 'The OnAccess OEM scanner requires App:OemStorageRoot.' }
    # Real-time scanning (e.g. OfficeScan) must watch the OEM storage; the application verifies it with an EICAR canary.
    Write-Host "OEM scanning uses the server antivirus real-time scan. Do not exclude $oemStorage from it."
}
$clamAvEnabled = $false
if ($oemScanner -and $oemScanner.Value.PSObject.Properties['Engine'] -and
    $oemScanner.Value.Engine -ieq 'ClamAV' -and $oemScanner.Value.Engine -cne 'ClamAV') {
    throw 'App:OemScanner:Engine must use the exact value ClamAV.'
}
if ($oemScanner -and $oemScanner.Value.PSObject.Properties['Engine'] -and $oemScanner.Value.Engine -ceq 'ClamAV') {
    $clamAvEnabled = $true
    $clamAvProperty = $oemScanner.Value.PSObject.Properties['ClamAv']
    if (!$clamAvProperty) { throw 'The ClamAv OEM scanner requires App:OemScanner:ClamAv.' }
    $clamAv = $clamAvProperty.Value
    foreach ($requiredName in @('Host','Port','ConnectTimeoutSeconds','MaxStreamBytes')) {
        if (!$clamAv.PSObject.Properties[$requiredName]) { throw "Missing App:OemScanner:ClamAv:$requiredName." }
    }
    if ($clamAv.Host -ne '127.0.0.1' -or [int]$clamAv.Port -ne 3310) { throw 'Bundled ClamD must use 127.0.0.1:3310.' }
    if ([int]$clamAv.ConnectTimeoutSeconds -lt 1 -or [int]$clamAv.ConnectTimeoutSeconds -gt 30) { throw 'ClamAv ConnectTimeoutSeconds must be between 1 and 30.' }
    if ([long]$clamAv.MaxStreamBytes -lt 1 -or [long]$clamAv.MaxStreamBytes -gt 1073741824) { throw 'ClamAv MaxStreamBytes must be at most the bundled 1 GiB INSTREAM limit.' }
    $ClamAvInstallRoot = FullPath $ClamAvInstallRoot
    $ClamAvDataRoot = FullPath $ClamAvDataRoot
    foreach ($path in @($ClamAvInstallRoot,$ClamAvDataRoot)) { NoLinks $path }
    foreach ($path in @($ClamAvInstallRoot,$ClamAvDataRoot)) {
        foreach ($other in @($PackageRoot,$SiteRoot,$ConfigPath,$storage,$oemStorage)) {
            if ($other -and ((Within $path $other) -or (Within $other $path))) { throw 'ClamAV program/data paths must be separate from package, site, configuration and business storage.' }
        }
    }
    if ((Within $ClamAvInstallRoot $ClamAvDataRoot) -or (Within $ClamAvDataRoot $ClamAvInstallRoot)) { throw 'ClamAV program and persistent data paths must be separate.' }
}
if ($UseExistingClamAv -and !$clamAvEnabled) { throw 'UseExistingClamAv is valid only when App:OemScanner:Engine is ClamAV.' }
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
foreach ($required in @('Yf.Api.dll','Yf.Api.runtimeconfig.json','web.config','wwwroot\index.html','maintenance-common.ps1','install-clamav.ps1','update-clamav.ps1','clamav-database.ps1','clamav\PROVENANCE.json','clamav\database-manifest.json','clamav\database\main.cvd','clamav\database\daily.cvd','clamav\database\bytecode.cvd')) {
    if (!(Test-Path -LiteralPath (Join-Path $PackageRoot $required) -PathType Leaf)) { throw "Missing required payload: $required" }
}
. (Join-Path $PackageRoot 'maintenance-common.ps1')
Assert-YfSeparate @($PackageRoot,$SiteRoot,$ConfigPath,$storage)
if ($oemStorage) { Assert-YfSeparate @($PackageRoot,$SiteRoot,$ConfigPath,$storage,$oemStorage) }
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

if ($clamAvEnabled) {
    if ($UseExistingClamAv) {
        Assert-ExistingClamAv $ClamAvInstallRoot $ClamAvDataRoot
    } else {
        & (Join-Path $PackageRoot 'install-clamav.ps1') -PackageRoot $PackageRoot -InstallRoot $ClamAvInstallRoot -DataRoot $ClamAvDataRoot -HostAddress '127.0.0.1' -Port 3310
    }
}

New-Item -ItemType Directory -Path $SiteRoot -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $PackageRoot -Force) {
    if ($item.Name -in @('install-iis.ps1','install-clamav.ps1','update-clamav.ps1','clamav-database.ps1','clamav','README.md','appsettings.example.json','manifest.json')) { continue }
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
if ($oemStorage) {
    & icacls.exe $oemStorage /grant "${identity}:(OI)(CI)M" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to grant OEM storage permissions.' }
}
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
