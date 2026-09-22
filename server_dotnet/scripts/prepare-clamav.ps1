#Requires -Version 5.1
<#
.SYNOPSIS
Prepare the pinned ClamAV Windows x64 portable distribution for a release.
.DESCRIPTION
Downloads only official ClamAV 1.4.6 release assets, verifies the SHA-256
digests published by the GitHub Releases API, optionally verifies detached
signatures when GnuPG is already available, and creates a self-contained
release payload including a signed official virus database snapshot. No service is installed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$CacheDirectory,
    [string]$DatabaseSnapshotDirectory,
    [switch]$UseExistingCacheOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$version = '1.4.6'
$releaseTag = 'clamav-1.4.6'
$releaseApi = 'https://api.github.com/repos/Cisco-Talos/clamav/releases/tags/clamav-1.4.6'
$talosKeyUrl = 'https://raw.githubusercontent.com/Cisco-Talos/clamav-documentation/main/src/manual/cisco-talos.gpg'
$talosKeySha256 = '5bfca75f112f04b89cd992f5f925018e8dfc28e0e7f6e33f6a4c33ed9e29d911'
$talosKeyFingerprint = '5BADCA2665EF59DCF8A23D8B707F0DB480836771'
$assets = @(
    [ordered]@{
        name = 'clamav-1.4.6.win.x64.zip'
        bytes = [long]191947200
        sha256 = '57b6fd1d60cd87bafe800f97407ecdef0576d36b3900b8b7abcfbbabe88295fd'
        url = 'https://github.com/Cisco-Talos/clamav/releases/download/clamav-1.4.6/clamav-1.4.6.win.x64.zip'
        purpose = 'Official Windows x64 portable binary distribution'
    },
    [ordered]@{
        name = 'clamav-1.4.6.win.x64.zip.sig'
        bytes = [long]801
        sha256 = '9348c66b512375b00a06adb63a50be90457b4cf1335ff398c1161651d962e50f'
        url = 'https://github.com/Cisco-Talos/clamav/releases/download/clamav-1.4.6/clamav-1.4.6.win.x64.zip.sig'
        purpose = 'Official detached signature for the binary distribution'
    },
    [ordered]@{
        name = 'clamav-1.4.6.tar.gz'
        bytes = [long]45990124
        sha256 = '06dbc7adf96e2f6a27c548a841ce83c95360d1e121b106a6f7cf56f282a3c61b'
        url = 'https://github.com/Cisco-Talos/clamav/releases/download/clamav-1.4.6/clamav-1.4.6.tar.gz'
        purpose = 'Matching complete corresponding source archive'
    },
    [ordered]@{
        name = 'clamav-1.4.6.tar.gz.sig'
        bytes = [long]801
        sha256 = 'f55ae2aa17482cf870f16578e56c2d77885c22ac748ebee9f6808c4dbba93bea'
        url = 'https://github.com/Cisco-Talos/clamav/releases/download/clamav-1.4.6/clamav-1.4.6.tar.gz.sig'
        purpose = 'Official detached signature for the source archive'
    }
)

function Get-FullLocalPath([string]$Value, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -notmatch '^[A-Za-z]:[\\/]') {
        throw "$Label must be an absolute local disk path."
    }
    $full = [IO.Path]::GetFullPath($Value).TrimEnd('\', '/')
    if ($full.Length -le 3) { throw "$Label cannot be a drive root." }
    if ($full -match '["\r\n]') { throw "$Label contains invalid configuration characters." }
    return $full
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

function Test-Within([string]$Child, [string]$Parent) {
    return $Child.Equals($Parent, [StringComparison]::OrdinalIgnoreCase) -or
        $Child.StartsWith($Parent + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    $encoding = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Assert-Artifact([string]$Path, [long]$Bytes, [string]$Sha256) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required cached artifact is missing: $Path" }
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -ne $Bytes) { throw "Unexpected byte length for $($item.Name): $($item.Length)" }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256) { throw "Upstream SHA-256 mismatch for $($item.Name)." }
}

function Receive-VerifiedArtifact($Asset, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        Assert-Artifact $Destination $Asset.bytes $Asset.sha256
        return
    }
    if ($UseExistingCacheOnly) { throw "Trusted cache is incomplete: $Destination" }
    $partial = $Destination + '.partial-' + [Guid]::NewGuid().ToString('N')
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $Asset.url -OutFile $partial
        Assert-Artifact $partial $Asset.bytes $Asset.sha256
        Move-Item -LiteralPath $partial -Destination $Destination
    }
    finally {
        if (Test-Path -LiteralPath $partial -PathType Leaf) { Remove-Item -LiteralPath $partial -Force }
    }
}

function Invoke-OptionalGpgVerification([string]$KeyPath, [string]$BinaryPath, [string]$BinarySignaturePath, [string]$SourcePath, [string]$SourceSignaturePath) {
    $gpg = Get-Command gpg -ErrorAction SilentlyContinue
    if (!$gpg) { return 'not-run-gpg-not-installed' }

    $gpgHome = Join-Path $cacheRoot ('.gpg-verify-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $gpgHome | Out-Null
    try {
        & $gpg.Source --batch --homedir $gpgHome --import $KeyPath 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Unable to import the pinned Cisco Talos GPG key.' }
        foreach ($pair in @(@($BinarySignaturePath, $BinaryPath), @($SourceSignaturePath, $SourcePath))) {
            $status = @(& $gpg.Source --batch --homedir $gpgHome --status-fd 1 --verify $pair[0] $pair[1] 2>&1) -join "`n"
            if ($LASTEXITCODE -ne 0 -or $status -notmatch ("VALIDSIG " + $talosKeyFingerprint)) {
                throw "Cisco Talos detached signature verification failed for $($pair[1])."
            }
        }
        return 'verified-cisco-talos-detached-signatures'
    }
    finally {
        if (Test-Path -LiteralPath $gpgHome -PathType Container) { Remove-Item -LiteralPath $gpgHome -Recurse -Force }
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\', '/')
$artifactsRoot = Join-Path $repoRoot '.artifacts'
$releasesRoot = Join-Path $repoRoot 'deloy'
if ([string]::IsNullOrWhiteSpace($CacheDirectory)) { $CacheDirectory = Join-Path $artifactsRoot 'cache\clamav' }
$cacheRoot = Get-FullLocalPath $CacheDirectory 'CacheDirectory'
$outputRoot = Get-FullLocalPath $OutputDirectory 'OutputDirectory'
Assert-NoReparsePoint $cacheRoot
Assert-NoReparsePoint $outputRoot
if (!(Test-Within $cacheRoot $artifactsRoot)) {
    throw "CacheDirectory must be inside the project artifact root: $artifactsRoot"
}
if (!((Test-Within $outputRoot $artifactsRoot) -and !$outputRoot.Equals($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) -and
    !((Test-Within $outputRoot $releasesRoot) -and !$outputRoot.Equals($releasesRoot, [StringComparison]::OrdinalIgnoreCase))) {
    throw "OutputDirectory must be below a project artifact root: $artifactsRoot or $releasesRoot"
}
if ((Test-Within $cacheRoot $outputRoot) -or (Test-Within $outputRoot $cacheRoot)) {
    throw 'CacheDirectory and OutputDirectory must be separate and cannot contain each other.'
}

if ((Test-Path -LiteralPath $outputRoot) -and !(Test-Path -LiteralPath $outputRoot -PathType Container)) {
    throw 'OutputDirectory points to a file.'
}
if ((Test-Path -LiteralPath $outputRoot) -and @(Get-ChildItem -LiteralPath $outputRoot -Force).Count) {
    throw 'OutputDirectory must be absent or empty; an existing payload is never overwritten.'
}
if (!(Test-Path -LiteralPath $cacheRoot -PathType Container)) {
    if ($UseExistingCacheOnly) { throw "CacheDirectory does not exist: $cacheRoot" }
    New-Item -ItemType Directory -Path $cacheRoot | Out-Null
}

foreach ($asset in $assets) {
    Receive-VerifiedArtifact $asset (Join-Path $cacheRoot $asset.name)
}

$keyPath = Join-Path $cacheRoot 'cisco-talos.gpg'
if (Test-Path -LiteralPath $keyPath -PathType Leaf) {
    $actualKeyHash = (Get-FileHash -LiteralPath $keyPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualKeyHash -ne $talosKeySha256) { throw 'Pinned Cisco Talos public key SHA-256 mismatch.' }
} else {
    if ($UseExistingCacheOnly) { throw "Trusted cache is missing the Cisco Talos public key: $keyPath" }
    $partialKey = $keyPath + '.partial-' + [Guid]::NewGuid().ToString('N')
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $talosKeyUrl -OutFile $partialKey
        $actualKeyHash = (Get-FileHash -LiteralPath $partialKey -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualKeyHash -ne $talosKeySha256) { throw 'Downloaded Cisco Talos public key SHA-256 mismatch.' }
        Move-Item -LiteralPath $partialKey -Destination $keyPath
    }
    finally {
        if (Test-Path -LiteralPath $partialKey -PathType Leaf) { Remove-Item -LiteralPath $partialKey -Force }
    }
}

$binaryPath = Join-Path $cacheRoot 'clamav-1.4.6.win.x64.zip'
$binarySignaturePath = "$binaryPath.sig"
$sourcePath = Join-Path $cacheRoot 'clamav-1.4.6.tar.gz'
$sourceSignaturePath = "$sourcePath.sig"
$signatureVerification = Invoke-OptionalGpgVerification $keyPath $binaryPath $binarySignaturePath $sourcePath $sourceSignaturePath

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($binaryPath)
try {
    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    foreach ($requiredName in @('clamd.exe', 'clamdscan.exe', 'freshclam.exe', 'COPYING.txt', 'README.md')) {
        if (!($entryNames | Where-Object { $_.EndsWith('/' + $requiredName, [StringComparison]::OrdinalIgnoreCase) })) {
            throw "Official portable archive is missing $requiredName."
        }
    }
}
finally { $archive.Dispose() }

# Tools are extracted anew from the hash-verified official archive, never from an
# unverified executable cache. Only the three signed CVD files enter the payload.
$workingRoot = Join-Path $cacheRoot ('.snapshot-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $workingRoot | Out-Null
    $toolRoot = Join-Path $workingRoot 'tools'
    [IO.Compression.ZipFile]::ExtractToDirectory($binaryPath, $toolRoot)
    $sigtools = @(Get-ChildItem -LiteralPath $toolRoot -Recurse -File -Filter 'sigtool.exe')
    $freshclams = @(Get-ChildItem -LiteralPath $toolRoot -Recurse -File -Filter 'freshclam.exe')
    if ($sigtools.Count -ne 1 -or $freshclams.Count -ne 1) { throw 'Unexpected ClamAV tools layout.' }
    $databaseHelper = Join-Path $repoRoot 'server_dotnet\deploy\clamav-database.ps1'
    . $databaseHelper
    if (![string]::IsNullOrWhiteSpace($DatabaseSnapshotDirectory)) {
        $databaseSource = Get-FullLocalPath $DatabaseSnapshotDirectory 'DatabaseSnapshotDirectory'
    } else {
        $databaseSource = Join-Path $cacheRoot 'database'
        if (!(Test-Path -LiteralPath $databaseSource -PathType Container)) {
            if ($UseExistingCacheOnly) { throw 'The offline cache has no virus database snapshot; supply DatabaseSnapshotDirectory or refresh the cache online.' }
            New-Item -ItemType Directory -Path $databaseSource | Out-Null
        }
        Assert-NoReparsePoint $databaseSource
        if (!$UseExistingCacheOnly) {
            $downloadConfig = Join-Path $workingRoot 'freshclam-snapshot.conf'
            $downloadLog = Join-Path $cacheRoot 'freshclam-snapshot.log'
            $downloadLines = @(
                ('DatabaseDirectory "' + $databaseSource + '"'),
                ('UpdateLogFile "' + $downloadLog + '"'),
                'DatabaseMirror database.clamav.net', 'ScriptedUpdates no',
                'TestDatabases yes', 'ConnectTimeout 30', 'ReceiveTimeout 60', 'MaxAttempts 2'
            )
            Write-Utf8NoBom $downloadConfig (($downloadLines -join "`n") + "`n")
            & $freshclams[0].FullName "--config-file=$downloadConfig" --quiet
            if ($LASTEXITCODE -ne 0) { throw "FreshClam could not refresh the signed database snapshot; see $downloadLog." }
        }
    }
    Assert-NoReparsePoint $databaseSource
    if ((Test-Within $databaseSource $outputRoot) -or (Test-Within $outputRoot $databaseSource)) {
        throw 'DatabaseSnapshotDirectory and OutputDirectory must be separate.'
    }
    $snapshotRoot = Join-Path $workingRoot 'database'
    New-Item -ItemType Directory -Path $snapshotRoot | Out-Null
    foreach ($name in @('main.cvd','daily.cvd','bytecode.cvd')) {
        $inputFile = Join-Path $databaseSource $name
        Assert-NoReparsePoint $inputFile
        if (!(Test-Path -LiteralPath $inputFile -PathType Leaf)) { throw "Signed snapshot requires $name; CLD/custom signature files cannot replace a signed CVD." }
        Copy-Item -LiteralPath $inputFile -Destination (Join-Path $snapshotRoot $name)
    }
    $snapshot = Get-ClamAvDatabaseSnapshot -DatabaseDirectory $snapshotRoot -SigtoolPath $sigtools[0].FullName
    if (!(Test-Path -LiteralPath $outputRoot)) { New-Item -ItemType Directory -Path $outputRoot | Out-Null }
    $snapshotManifest = Join-Path $workingRoot 'database-manifest.json'
    Write-Utf8NoBom $snapshotManifest (($snapshot | ConvertTo-Json -Depth 8) + "`n")
    $verifiedSnapshot = Copy-ClamAvDatabaseSnapshot -SourceDirectory $snapshotRoot -ManifestPath $snapshotManifest `
        -DestinationDirectory (Join-Path $outputRoot 'database') -SigtoolPath $sigtools[0].FullName
    Write-Utf8NoBom (Join-Path $outputRoot 'database-manifest.json') (($verifiedSnapshot | ConvertTo-Json -Depth 8) + "`n")
}
finally {
    $resolvedWorking = [IO.Path]::GetFullPath($workingRoot)
    if (!$resolvedWorking.StartsWith($cacheRoot + '\.snapshot-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe ClamAV scratch cleanup path.' }
    if (Test-Path -LiteralPath $resolvedWorking) { Remove-Item -LiteralPath $resolvedWorking -Recurse -Force }
}

$distributionRoot = Join-Path $outputRoot 'distribution'
New-Item -ItemType Directory -Path $distributionRoot | Out-Null
foreach ($asset in $assets) { Copy-Item -LiteralPath (Join-Path $cacheRoot $asset.name) -Destination $distributionRoot }
Copy-Item -LiteralPath $keyPath -Destination (Join-Path $distributionRoot 'cisco-talos.gpg')

$clamdTemplate = @'
# Generated for the Yf.System private loopback scanning service.
LogFile "{{LOG_DIRECTORY}}/clamd.log"
LogFileUnlock yes
LogFileMaxSize 20M
LogTime yes
LogRotate yes
DatabaseDirectory "{{DATABASE_DIRECTORY}}"
TemporaryDirectory "{{TEMP_DIRECTORY}}"
TCPSocket 3310
TCPAddr 127.0.0.1
MaxConnectionQueueLength 30
StreamMaxLength 1G
ReadTimeout 120
CommandReadTimeout 30
SendBufTimeout 500
MaxThreads 2
MaxQueue 4
SelfCheck 600
AlertEncrypted yes
AlertExceedsMax yes
MaxScanTime 120000
MaxScanSize 1536M
MaxFileSize 1G
MaxFiles 10000
MaxRecursion 16
PCREMaxFileSize 1G
'@
Write-Utf8NoBom (Join-Path $outputRoot 'clamd.conf.template') ($clamdTemplate.Trim() + "`n")

$freshclamTemplate = @'
# Generated for the Yf.System ClamAV signature update service.
DatabaseDirectory "{{DATABASE_DIRECTORY}}"
UpdateLogFile "{{LOG_DIRECTORY}}/freshclam.log"
LogFileMaxSize 20M
LogTime yes
LogRotate yes
DatabaseMirror database.clamav.net
Checks 12
NotifyClamd "{{CLAMD_CONFIG}}"
'@
Write-Utf8NoBom (Join-Path $outputRoot 'freshclam.conf.template') ($freshclamTemplate.Trim() + "`n")

$provenance = [ordered]@{
    schemaVersion = 1
    component = 'ClamAV'
    version = $version
    platform = 'Windows x64'
    releaseTag = $releaseTag
    releaseApi = $releaseApi
    sourceRepository = 'https://github.com/Cisco-Talos/clamav'
    license = 'GPL-2.0-only'
    completeCorrespondingSource = 'distribution/clamav-1.4.6.tar.gz'
    databaseSnapshotManifest = 'database-manifest.json'
    verification = [ordered]@{
        pinnedUpstreamDigests = 'verified'
        detachedSignatures = $signatureVerification
        talosKeyUrl = $talosKeyUrl
        talosKeySha256 = $talosKeySha256
        talosKeyFingerprint = $talosKeyFingerprint
        authenticode = 'not-applicable-official-portable-executables-are-not-authenticode-signed'
    }
    assets = $assets
}
Write-Utf8NoBom (Join-Path $outputRoot 'PROVENANCE.json') (($provenance | ConvertTo-Json -Depth 8) + "`n")

$readme = @'
# Bundled ClamAV 1.4.6 LTS

This payload contains the unmodified official Windows x64 portable ZIP, its
detached signature, the matching complete corresponding source archive and
signature, and the pinned Cisco Talos public key. `PROVENANCE.json` records the
official release URLs and the SHA-256 digests published by the GitHub Releases
API. The installer extracts the upstream archive outside the IIS site and keeps
the signature database and logs in a separate persistent data directory.

The signed main.cvd, daily.cvd and bytecode.cvd snapshot in database/ is verified
by the official sigtool, then recorded in database-manifest.json with sizes,
SHA-256, signature counts, versions and UTC build times. First installation uses
this snapshot without downloading files. FreshClam updates it after deployment;
the application's configured stale-database policy continues to apply offline.

`clamd` accepts TCP only on `127.0.0.1:3310`. `INSTREAM` is limited to 1 GiB.
Files or expanded content that exceed the configured limits are alerts because
`AlertExceedsMax yes` is enabled; encrypted archives/documents alert because
`AlertEncrypted yes` is enabled. A 20 GiB business upload must therefore be
rejected before scanning and must never be reported clean after a silent skip.

The portable executable is a separate GPL-licensed process communicating over
the clamd protocol; it is not linked into Yf.Api. The unmodified upstream
`COPYING.txt`, dependency notices and manuals remain inside the official ZIP.
'@
Write-Utf8NoBom (Join-Path $outputRoot 'README.md') ($readme.Trim() + "`n")

Write-Host "ClamAV payload: $outputRoot"
Write-Host "Verified binary cache: $binaryPath"
Write-Host "Pinned upstream digest: $($assets[0].sha256)"
Write-Host "Detached signature verification: $signatureVerification"
