#Requires -Version 5.1
<#
.SYNOPSIS
Validate and copy a signed three-file ClamAV CVD database snapshot.
.DESCRIPTION
This helper accepts only main.cvd, daily.cvd and bytecode.cvd. It validates
each file with the pinned official ClamAV 1.4.6 Windows x64 sigtool, parses the
signed CVD header, and never treats database age as signature validity.
#>
Set-StrictMode -Version 2.0

$script:ClamAvDatabaseFiles = @('main.cvd','daily.cvd','bytecode.cvd')
$script:ClamAvSigtoolSha256 = '1e6088d179dc0f55839f9286588e96664d107112decdce6147107228cbbcf313'

function Resolve-ClamAvLocalPath([string]$Value,[string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -notmatch '^[A-Za-z]:[\\/]') {
        throw "$Label must be an absolute local disk path."
    }
    $full = [IO.Path]::GetFullPath($Value).TrimEnd('\','/')
    if ($full.Length -le 3) { throw "$Label cannot be a drive root." }
    return $full
}

function Assert-ClamAvNoReparsePoint([string]$Path) {
    $cursor = $Path
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and
            ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Reparse points are not allowed for ClamAV database verification: $Path"
        }
        $cursor = Split-Path -Parent $cursor
    }
}

function Test-ClamAvWithin([string]$Child,[string]$Parent) {
    return $Child.Equals($Parent,[StringComparison]::OrdinalIgnoreCase) -or
        $Child.StartsWith($Parent + '\',[StringComparison]::OrdinalIgnoreCase)
}

function Get-ClamAvStreamSha256([IO.Stream]$Stream) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $Stream.Position = 0
        return ([BitConverter]::ToString($algorithm.ComputeHash($Stream))).Replace('-','').ToLowerInvariant()
    }
    finally { $algorithm.Dispose() }
}

function Read-ClamAvCvdHeader([IO.Stream]$Stream,[string]$Path) {
    if ($Stream.Length -lt 512) { throw "CVD file is shorter than its signed 512-byte header: $Path" }
    $Stream.Position = 0
    $bytes = New-Object byte[] 512
    if ($Stream.Read($bytes,0,512) -ne 512) { throw "Unable to read the complete CVD header: $Path" }
    $header = [Text.Encoding]::ASCII.GetString($bytes).TrimEnd([char]0,' ')
    $fields = $header.Split(':')
    if ($fields.Count -ne 9 -or $fields[0] -ne 'ClamAV-VDB') { throw "Invalid or unknown CVD header format: $Path" }
    $version = 0; $signatures = 0; $functionalityLevel = 0; $epoch = [long]0
    if (![int]::TryParse($fields[2],[Globalization.NumberStyles]::None,[Globalization.CultureInfo]::InvariantCulture,[ref]$version) -or $version -le 0 -or
        ![int]::TryParse($fields[3],[Globalization.NumberStyles]::None,[Globalization.CultureInfo]::InvariantCulture,[ref]$signatures) -or $signatures -le 0 -or
        ![int]::TryParse($fields[4],[Globalization.NumberStyles]::None,[Globalization.CultureInfo]::InvariantCulture,[ref]$functionalityLevel) -or $functionalityLevel -le 0 -or
        ![long]::TryParse($fields[8],[Globalization.NumberStyles]::None,[Globalization.CultureInfo]::InvariantCulture,[ref]$epoch) -or $epoch -le 0) {
        throw "Invalid numeric metadata in CVD header: $Path"
    }
    if ($fields[5] -notmatch '^[0-9a-fA-F]{32}$' -or [string]::IsNullOrWhiteSpace($fields[6]) -or [string]::IsNullOrWhiteSpace($fields[7])) {
        throw "Incomplete signed metadata in CVD header: $Path"
    }
    try { $builtAt = [DateTimeOffset]::FromUnixTimeSeconds($epoch).UtcDateTime }
    catch { throw "Invalid CVD build epoch: $Path" }
    if ($builtAt -gt [DateTime]::UtcNow) { throw "CVD build timestamp is in the future: $Path" }
    return [pscustomobject]@{
        version = $version
        signatures = $signatures
        functionalityLevel = $functionalityLevel
        builtAtUtc = $builtAt.ToString('o',[Globalization.CultureInfo]::InvariantCulture)
    }
}

function Assert-ClamAvSnapshotMatchesManifest($Snapshot,$Manifest,[string]$Label) {
    if (!$Manifest -or [int]$Manifest.schemaVersion -ne 1) { throw "$Label has an unsupported schemaVersion." }
    $actualFiles = @($Snapshot.files)
    $manifestFiles = @($Manifest.files)
    if ($actualFiles.Count -ne 3 -or $manifestFiles.Count -ne 3) { throw "$Label must describe exactly three CVD files." }
    foreach ($actual in $actualFiles) {
        $matches = @($manifestFiles | Where-Object { $_.name -ceq $actual.name })
        if ($matches.Count -ne 1) { throw "$Label does not contain exactly one entry for $($actual.name)." }
        $expected = $matches[0]
        if ([long]$expected.bytes -ne [long]$actual.bytes -or
            [string]$expected.sha256 -cne [string]$actual.sha256 -or
            [int]$expected.version -ne [int]$actual.version -or
            [int]$expected.signatures -ne [int]$actual.signatures -or
            [int]$expected.functionalityLevel -ne [int]$actual.functionalityLevel) {
            throw "$Label metadata mismatch for $($actual.name)."
        }
        $expectedTime = [DateTimeOffset]::MinValue
        if (![DateTimeOffset]::TryParse([string]$expected.builtAtUtc,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::AssumeUniversal,[ref]$expectedTime) -or
            $expectedTime.UtcDateTime.ToString('o') -cne [string]$actual.builtAtUtc) {
            throw "$Label build timestamp mismatch for $($actual.name)."
        }
    }
}

function Get-ClamAvDatabaseSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$DatabaseDirectory,
        [Parameter(Mandatory=$true)][string]$SigtoolPath
    )
    $databaseRoot = Resolve-ClamAvLocalPath $DatabaseDirectory 'DatabaseDirectory'
    $sigtool = Resolve-ClamAvLocalPath $SigtoolPath 'SigtoolPath'
    Assert-ClamAvNoReparsePoint $databaseRoot
    Assert-ClamAvNoReparsePoint $sigtool
    if (!(Test-Path -LiteralPath $databaseRoot -PathType Container)) { throw "ClamAV database directory is missing: $databaseRoot" }
    if (!(Test-Path -LiteralPath $sigtool -PathType Leaf)) { throw "sigtool.exe is missing: $sigtool" }
    $entries = @(Get-ChildItem -LiteralPath $databaseRoot -Force)
    if ($entries.Count -ne 3 -or @($entries | Where-Object { !$_.PSIsContainer -and $script:ClamAvDatabaseFiles -ccontains $_.Name }).Count -ne 3) {
        throw 'DatabaseDirectory must contain only main.cvd, daily.cvd and bytecode.cvd.'
    }
    $sigtoolLock = [IO.File]::Open($sigtool,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if ((Get-ClamAvStreamSha256 $sigtoolLock) -ne $script:ClamAvSigtoolSha256) {
            throw 'sigtool.exe does not match the pinned official ClamAV 1.4.6 Windows x64 executable.'
        }
        $files = @()
        foreach ($name in $script:ClamAvDatabaseFiles) {
            $path = Join-Path $databaseRoot $name
            Assert-ClamAvNoReparsePoint $path
            # Keep one non-write/non-delete shared handle from signature check
            # through header parsing and hashing to prevent a mixed-file result.
            $stream = [IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            try {
                $info = @(& $sigtool --info $path 2>&1) -join "`n"
                if ($LASTEXITCODE -ne 0 -or $info -notmatch '(?m)^Verification OK\.\s*$') {
                    throw "Official CVD signature verification failed for $name."
                }
                $header = Read-ClamAvCvdHeader $stream $path
                if ($info -notmatch ("(?m)^Version:\s*" + $header.version + "\s*$") -or
                    $info -notmatch ("(?m)^Signatures:\s*" + $header.signatures + "\s*$") -or
                    $info -notmatch ("(?m)^Functionality level:\s*" + $header.functionalityLevel + "\s*$")) {
                    throw "sigtool metadata and signed CVD header disagree for $name."
                }
                $files += [pscustomobject][ordered]@{
                    name = $name
                    bytes = [long]$stream.Length
                    sha256 = Get-ClamAvStreamSha256 $stream
                    version = [int]$header.version
                    signatures = [int]$header.signatures
                    functionalityLevel = [int]$header.functionalityLevel
                    builtAtUtc = [string]$header.builtAtUtc
                }
            }
            finally { $stream.Dispose() }
        }
        return [pscustomobject][ordered]@{ schemaVersion = 1; files = $files }
    }
    finally { $sigtoolLock.Dispose() }
}

function Copy-ClamAvDatabaseSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$SourceDirectory,
        [Parameter(Mandatory=$true)][string]$ManifestPath,
        [Parameter(Mandatory=$true)][string]$DestinationDirectory,
        [Parameter(Mandatory=$true)][string]$SigtoolPath
    )
    $sourceRoot = Resolve-ClamAvLocalPath $SourceDirectory 'SourceDirectory'
    $manifestFile = Resolve-ClamAvLocalPath $ManifestPath 'ManifestPath'
    $destinationRoot = Resolve-ClamAvLocalPath $DestinationDirectory 'DestinationDirectory'
    foreach ($path in @($sourceRoot,$manifestFile,$destinationRoot)) { Assert-ClamAvNoReparsePoint $path }
    if ((Test-ClamAvWithin $destinationRoot $sourceRoot) -or (Test-ClamAvWithin $sourceRoot $destinationRoot)) {
        throw 'SourceDirectory and DestinationDirectory must be separate and cannot contain each other.'
    }
    if (!(Test-Path -LiteralPath $manifestFile -PathType Leaf)) { throw "ClamAV database manifest is missing: $manifestFile" }
    if ((Test-Path -LiteralPath $destinationRoot) -and !(Test-Path -LiteralPath $destinationRoot -PathType Container)) { throw 'DestinationDirectory points to a file.' }
    if ((Test-Path -LiteralPath $destinationRoot -PathType Container) -and @(Get-ChildItem -LiteralPath $destinationRoot -Force).Count) {
        throw 'DestinationDirectory must be absent or empty and is never overwritten.'
    }
    $sourceSnapshot = Get-ClamAvDatabaseSnapshot -DatabaseDirectory $sourceRoot -SigtoolPath $SigtoolPath
    try { $manifest = Get-Content -LiteralPath $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "ClamAV database manifest is not valid JSON: $manifestFile" }
    Assert-ClamAvSnapshotMatchesManifest $sourceSnapshot $manifest 'ClamAV database manifest'

    $createdDestination = !(Test-Path -LiteralPath $destinationRoot)
    $copied = @()
    try {
        if ($createdDestination) { New-Item -ItemType Directory -Path $destinationRoot | Out-Null }
        foreach ($name in $script:ClamAvDatabaseFiles) {
            $destination = Join-Path $destinationRoot $name
            Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination $destination
            $copied += $destination
        }
        $destinationSnapshot = Get-ClamAvDatabaseSnapshot -DatabaseDirectory $destinationRoot -SigtoolPath $SigtoolPath
        Assert-ClamAvSnapshotMatchesManifest $destinationSnapshot $manifest 'Copied ClamAV database snapshot'
        return $destinationSnapshot
    }
    catch {
        $failure = $_
        foreach ($path in $copied) { if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force } }
        if ($createdDestination -and (Test-Path -LiteralPath $destinationRoot -PathType Container) -and !@(Get-ChildItem -LiteralPath $destinationRoot -Force).Count) {
            Remove-Item -LiteralPath $destinationRoot -Force
        }
        throw $failure
    }
}
