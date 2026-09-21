#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
Install the bundled ClamAV engine and updater as independent Windows services.
.DESCRIPTION
Fresh installation only. Existing services, a listener on the requested port,
or existing target directories are refused. ClamD starts from the signed CVD
snapshot bundled with the release; FreshClam then maintains it in background.
#>
[CmdletBinding()]
param(
    [string]$PackageRoot = $PSScriptRoot,
    [string]$InstallRoot = 'C:\Program Files\YfSystem\ClamAV',
    [string]$DataRoot = 'C:\ProgramData\YfSystem\ClamAV',
    [ValidateSet('127.0.0.1')][string]$HostAddress = '127.0.0.1',
    [ValidateRange(1, 65535)][int]$Port = 3310
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Get-FullLocalPath([string]$Value, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -notmatch '^[A-Za-z]:[\\/]') {
        throw "$Label must be an absolute local disk path."
    }
    $full = [IO.Path]::GetFullPath($Value).TrimEnd('\', '/')
    if ($full.Length -le 3) { throw "$Label cannot be a drive root." }
    if ($full.Contains('"')) { throw "$Label cannot contain a quote." }
    return $full
}

function Test-Within([string]$Child, [string]$Parent) {
    return $Child.Equals($Parent, [StringComparison]::OrdinalIgnoreCase) -or
        $Child.StartsWith($Parent + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoReparsePoint([string]$Path) {
    $cursor = $Path
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and
            ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Reparse points are not allowed: $Path"
        }
        $cursor = Split-Path -Parent $cursor
    }
}

function Assert-AbsentPath([string]$Path, [string]$Label) {
    if (Test-Path -LiteralPath $Path) { throw "$Label already exists and will not be overwritten." }
}

function Test-TcpListener([string]$Address, [int]$TcpPort) {
    $client = New-Object Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect($Address, $TcpPort, $null, $null)
        if (!$async.AsyncWaitHandle.WaitOne(500)) { return $false }
        try { $client.EndConnect($async); return $true } catch { return $false }
    }
    finally { $client.Dispose() }
}

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    $encoding = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Expand-Configuration([string]$TemplatePath, [string]$DestinationPath, [hashtable]$Values) {
    $content = Get-Content -LiteralPath $TemplatePath -Raw -Encoding UTF8
    foreach ($key in $Values.Keys) { $content = $content.Replace('{{' + $key + '}}', [string]$Values[$key]) }
    if ($content -match '\{\{[^}]+\}\}') { throw "Unresolved configuration token in $TemplatePath." }
    Write-Utf8NoBom $DestinationPath $content
}

function Invoke-ClamDCommand([string]$Address, [int]$TcpPort, [string]$Command) {
    $client = New-Object Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect($Address, $TcpPort, $null, $null)
        if (!$async.AsyncWaitHandle.WaitOne(1000)) { throw 'connect timeout' }
        $client.EndConnect($async)
        $stream = $client.GetStream()
        $stream.ReadTimeout = 3000
        $request = [Text.Encoding]::ASCII.GetBytes(('z' + $Command + "`0"))
        $stream.Write($request, 0, $request.Length)
        $buffer = New-Object byte[] 1024
        $read = $stream.Read($buffer, 0, $buffer.Length)
        return [Text.Encoding]::ASCII.GetString($buffer, 0, $read).Trim([char]0)
    }
    finally { $client.Dispose() }
}

function Wait-ClamDReady([string]$Address, [int]$TcpPort, [int]$TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            if ((Invoke-ClamDCommand $Address $TcpPort 'PING') -eq 'PONG') {
                $versionReply = Invoke-ClamDCommand $Address $TcpPort 'VERSION'
                if ($versionReply -match '^ClamAV 1\.4\.6/[1-9][0-9]*/.+$') { return $versionReply }
            }
        } catch { }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "ClamD did not return PONG plus a ClamAV 1.4.6 VERSION with a loaded database on $Address`:$TcpPort within $TimeoutSeconds seconds."
}

if ($Port -ne 3310) { throw 'The bundled clamd.conf is pinned to TCP port 3310.' }
$PackageRoot = Get-FullLocalPath $PackageRoot 'PackageRoot'
$InstallRoot = Get-FullLocalPath $InstallRoot 'InstallRoot'
$DataRoot = Get-FullLocalPath $DataRoot 'DataRoot'
foreach ($path in @($PackageRoot, $InstallRoot, $DataRoot)) { Assert-NoReparsePoint $path }
foreach ($pair in @(
    @($InstallRoot, $PackageRoot), @($PackageRoot, $InstallRoot),
    @($DataRoot, $PackageRoot), @($PackageRoot, $DataRoot),
    @($InstallRoot, $DataRoot), @($DataRoot, $InstallRoot)
)) {
    if (Test-Within $pair[0] $pair[1]) { throw 'PackageRoot, InstallRoot and DataRoot must be separate directories.' }
}
Assert-AbsentPath $InstallRoot 'InstallRoot'
Assert-AbsentPath $DataRoot 'DataRoot'

foreach ($serviceName in @('clamd', 'freshclam')) {
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
        throw "Windows service '$serviceName' already exists; it will not be replaced or reconfigured."
    }
}
if (Test-TcpListener $HostAddress $Port) {
    throw "TCP $HostAddress`:$Port already has a listener; no process will be stopped."
}

$bundleRoot = Join-Path $PackageRoot 'clamav'
$databaseHelper = Join-Path $PackageRoot 'clamav-database.ps1'
if (!(Test-Path -LiteralPath $databaseHelper -PathType Leaf)) { throw 'Bundled ClamAV database helper is missing.' }
. $databaseHelper
$provenancePath = Join-Path $bundleRoot 'PROVENANCE.json'
if (!(Test-Path -LiteralPath $provenancePath -PathType Leaf)) { throw 'Bundled ClamAV provenance is missing.' }
$provenance = Get-Content -LiteralPath $provenancePath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($provenance.version -ne '1.4.6' -or $provenance.platform -ne 'Windows x64') { throw 'Unexpected bundled ClamAV version or platform.' }
$binaryAsset = @($provenance.assets | Where-Object { $_.name -eq 'clamav-1.4.6.win.x64.zip' })
if ($binaryAsset.Count -ne 1) { throw 'Pinned ClamAV binary metadata is missing or ambiguous.' }
$expectedBinaryBytes = [long]191947200
$expectedBinarySha256 = '57b6fd1d60cd87bafe800f97407ecdef0576d36b3900b8b7abcfbbabe88295fd'
if ([long]$binaryAsset[0].bytes -ne $expectedBinaryBytes -or [string]$binaryAsset[0].sha256 -ne $expectedBinarySha256) {
    throw 'Bundled provenance does not match the installer-pinned ClamAV binary identity.'
}
$archivePath = Join-Path $bundleRoot 'distribution\clamav-1.4.6.win.x64.zip'
if (!(Test-Path -LiteralPath $archivePath -PathType Leaf)) { throw 'Bundled ClamAV portable archive is missing.' }
if ((Get-Item -LiteralPath $archivePath).Length -ne $expectedBinaryBytes -or
    (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedBinarySha256) {
    throw 'Bundled ClamAV portable archive does not match pinned upstream metadata.'
}

$installParent = Split-Path -Parent $InstallRoot
$dataParent = Split-Path -Parent $DataRoot
foreach ($parent in @($installParent, $dataParent)) {
    if (!(Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Assert-NoReparsePoint $parent
}
$stagingId = [Guid]::NewGuid().ToString('N')
$stageInstallRoot = Join-Path $installParent ((Split-Path -Leaf $InstallRoot) + '.prepare-' + $stagingId)
$stageDataRoot = Join-Path $dataParent ((Split-Path -Leaf $DataRoot) + '.prepare-' + $stagingId)
$createdServices = @()
$movedInstall = $false
$movedData = $false
try {
    New-Item -ItemType Directory -Path $stageInstallRoot | Out-Null
    New-Item -ItemType Directory -Path $stageDataRoot | Out-Null
    # Restrict staging before extracting or executing anything: custom parent
    # directories may otherwise grant ordinary users write access to the engine.
    foreach ($ownedRoot in @($stageInstallRoot,$stageDataRoot)) {
        & icacls.exe $ownedRoot /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Unable to restrict ClamAV staging ACL: $ownedRoot" }
    }
    foreach ($name in @('database', 'logs', 'temp')) { New-Item -ItemType Directory -Path (Join-Path $stageDataRoot $name) | Out-Null }
    Expand-Archive -LiteralPath $archivePath -DestinationPath $stageInstallRoot
    $stageClamdExe = @(Get-ChildItem -LiteralPath $stageInstallRoot -Recurse -File -Filter 'clamd.exe')
    $stageFreshclamExe = @(Get-ChildItem -LiteralPath $stageInstallRoot -Recurse -File -Filter 'freshclam.exe')
    if ($stageClamdExe.Count -ne 1 -or $stageFreshclamExe.Count -ne 1 -or $stageClamdExe[0].DirectoryName -ne $stageFreshclamExe[0].DirectoryName) {
        throw 'The official portable archive has an unexpected executable layout.'
    }
    $relativeProgramRoot = $stageClamdExe[0].DirectoryName.Substring($stageInstallRoot.Length).TrimStart('\', '/')
    $programRoot = Join-Path $InstallRoot $relativeProgramRoot
    $clamdExe = Join-Path $programRoot 'clamd.exe'
    $freshclamExe = Join-Path $programRoot 'freshclam.exe'
    $databaseRoot = Join-Path $DataRoot 'database'
    $logRoot = Join-Path $DataRoot 'logs'
    $tempRoot = Join-Path $DataRoot 'temp'
    $clamdConfig = Join-Path $programRoot 'clamd.conf'
    $freshclamConfig = Join-Path $programRoot 'freshclam.conf'
    $finalConfigValues = @{
        DATABASE_DIRECTORY = $databaseRoot
        LOG_DIRECTORY = $logRoot
        TEMP_DIRECTORY = $tempRoot
        CLAMD_CONFIG = $clamdConfig
    }
    Expand-Configuration (Join-Path $bundleRoot 'clamd.conf.template') (Join-Path $stageClamdExe[0].DirectoryName 'clamd.conf') $finalConfigValues
    Expand-Configuration (Join-Path $bundleRoot 'freshclam.conf.template') (Join-Path $stageFreshclamExe[0].DirectoryName 'freshclam.conf') $finalConfigValues

    # Install only the release snapshot that was signed and matched against its
    # immutable manifest. No network access is needed for initial readiness.
    $databaseSnapshot = Copy-ClamAvDatabaseSnapshot `
        -SourceDirectory (Join-Path $bundleRoot 'database') `
        -ManifestPath (Join-Path $bundleRoot 'database-manifest.json') `
        -DestinationDirectory (Join-Path $stageDataRoot 'database') `
        -SigtoolPath (Join-Path $stageClamdExe[0].DirectoryName 'sigtool.exe')
    Write-Utf8NoBom (Join-Path $stageDataRoot 'database-snapshot-manifest.json') (($databaseSnapshot | ConvertTo-Json -Depth 6) + "`n")

    foreach ($serviceName in @('clamd', 'freshclam')) {
        if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
            throw "Windows service '$serviceName' appeared during installation; it will not be changed."
        }
    }
    # Use explicit service creation. ClamAV's --install-service first deletes a
    # same-named service, which is unsafe for a shared target host.
    $clamdBinaryPath = '"' + $clamdExe + '" --daemon --service-mode --config-file="' + $clamdConfig + '"'
    $freshclamBinaryPath = '"' + $freshclamExe + '" --daemon --service-mode --config-file="' + $freshclamConfig + '"'
    New-Service -Name 'clamd' -BinaryPathName $clamdBinaryPath -DisplayName 'ClamAV ClamD' -Description 'Provides virus scanning facilities for Yf.System through loopback-only ClamD.' -StartupType Automatic | Out-Null
    $createdServices += 'clamd'
    New-Service -Name 'freshclam' -BinaryPathName $freshclamBinaryPath -DisplayName 'ClamAV FreshClam' -Description 'Keeps the ClamAV signature database current for Yf.System.' -StartupType Automatic | Out-Null
    $createdServices += 'freshclam'
    & sc.exe failure freshclam 'reset=' 86400 'actions=' 'restart/600000' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to configure FreshClam service recovery.' }

    Move-Item -LiteralPath $stageInstallRoot -Destination $InstallRoot
    $movedInstall = $true
    Move-Item -LiteralPath $stageDataRoot -Destination $DataRoot
    $movedData = $true

    foreach ($ownedRoot in @($InstallRoot,$DataRoot)) {
        & icacls.exe $ownedRoot /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Unable to restrict ClamAV directory ACL: $ownedRoot" }
    }

    Start-Service -Name 'clamd'
    $versionReply = Wait-ClamDReady $HostAddress $Port 60
    $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='clamd'"
    if (!$serviceInfo -or [int]$serviceInfo.ProcessId -le 0) { throw 'ClamD service has no running process.' }
    $listener = @(Get-NetTCPConnection -LocalAddress $HostAddress -LocalPort $Port -State Listen -ErrorAction Stop | Where-Object { $_.OwningProcess -eq [int]$serviceInfo.ProcessId })
    if ($listener.Count -ne 1) { throw 'The loopback ClamD listener is not owned by the clamd Windows service process.' }
    try {
        Start-Service -Name 'freshclam'
        Start-Sleep -Seconds 1
        if ((Get-Service -Name 'freshclam').Status -ne 'Running') {
            Write-Warning 'ClamD loaded the bundled signed snapshot, but FreshClam stopped before its background update remained active. Service recovery will retry; run update-clamav.ps1 after network recovery.'
        }
    }
    catch { Write-Warning "ClamD loaded the bundled signed snapshot, but FreshClam could not start its background update yet. Service recovery will retry; run update-clamav.ps1 after network recovery. $($_.Exception.Message)" }

    Write-Host "ClamAV 1.4.6 installed: $programRoot"
    Write-Host "Persistent database and logs: $DataRoot"
    Write-Host "ClamD ready on $HostAddress`:$Port ($versionReply)"
}
catch {
    $failure = $_
    foreach ($serviceName in @('freshclam', 'clamd')) {
        if ($createdServices -contains $serviceName) {
            try { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue } catch { }
            & sc.exe delete $serviceName 2>&1 | Out-Null
        }
    }
    foreach ($ownedPath in @($stageInstallRoot, $stageDataRoot)) {
        if (Test-Path -LiteralPath $ownedPath) { Remove-Item -LiteralPath $ownedPath -Recurse -Force }
    }
    if ($movedInstall -and (Test-Path -LiteralPath $InstallRoot)) { Remove-Item -LiteralPath $InstallRoot -Recurse -Force }
    if ($movedData -and (Test-Path -LiteralPath $DataRoot)) { Remove-Item -LiteralPath $DataRoot -Recurse -Force }
    throw $failure
}
