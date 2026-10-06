#Requires -Version 5.1
<#
.SYNOPSIS
Build a fresh, verifiable IIS deployment package for Yf.System.
.DESCRIPTION
Builds the React frontend and the .NET 8 ASP.NET Core API. It does not install or
configure IIS. By default it creates one uniquely named package directory below
repo/deloy. An explicit output must also be a new child directory there.
Only the deployment directory is produced unless CreateArchive is specified.
#>
[CmdletBinding()]
param(
    [string]$FreshOutputDirectory,
    [switch]$CreateArchive,
    [switch]$ExternalConfigurationTemplate,
    [switch]$AllowInsecurePrivateConfiguration,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Get-FullLocalPath([string]$Value, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -notmatch '^[A-Za-z]:[\\/]') {
        throw "$Label must be an absolute local disk path."
    }

    $full = [IO.Path]::GetFullPath($Value).TrimEnd('\', '/')
    if ($full.Length -le 3) {
        throw "$Label cannot be a drive root."
    }
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
            throw "Reparse points are not allowed in the release path: $Path"
        }
        $cursor = Split-Path -Parent $cursor
    }
}

function Invoke-Native([string]$FilePath, [string[]]$Arguments) {
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath exited with code $LASTEXITCODE."
    }
}

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    $encoding = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Protect-ReleasePath([string]$Path, [switch]$File) {
    $acl = if ($File) { New-Object Security.AccessControl.FileSecurity } else { New-Object Security.AccessControl.DirectorySecurity }
    $acl.SetAccessRuleProtection($true, $false)
    $sids = @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User,
        (New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'),
        (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544')
    )
    foreach ($sid in $sids) {
        $inheritance = if ($File) { 'None' } else { 'ContainerInherit,ObjectInherit' }
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', $inheritance, 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function New-RandomBase64([int]$ByteCount) {
    $bytes = New-Object byte[] $ByteCount
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($bytes) } finally { $random.Dispose() }
    return [Convert]::ToBase64String($bytes)
}

function Initialize-PublishDefaults([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'Local publish defaults file is missing: server_dotnet/deploy/publish-defaults.local.json'
    }
    $defaults = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$defaults.App -or [string]::IsNullOrWhiteSpace([string]$defaults.App.ConnectionString)) {
        throw 'Local publish defaults must contain App.ConnectionString.'
    }
    if ([string]::IsNullOrWhiteSpace([string]$defaults.App.JwtSecret)) {
        $defaults.App.JwtSecret = New-RandomBase64 32
        Write-Utf8NoBom $Path (($defaults | ConvertTo-Json -Depth 8) + "`n")
    }
    if ([Text.Encoding]::UTF8.GetByteCount([string]$defaults.App.JwtSecret) -lt 32) {
        throw 'Local publishing JWT secret must be at least 32 bytes.'
    }
    return $defaults
}

function Assert-PrivatePublishDefaults($Defaults, [switch]$AllowInsecure) {
    $origin = $null
    if (![Uri]::TryCreate([string]$Defaults.App.WebBaseUrl, [UriKind]::Absolute, [ref]$origin) -or
        $origin.AbsolutePath -ne '/' -or $origin.Query -or $origin.Fragment -or $origin.UserInfo -or
        $origin.Host -eq 'yf.example.com') {
        throw 'Private publication requires a real origin in App.WebBaseUrl.'
    }
    if ([string]::IsNullOrWhiteSpace([string]$Defaults.App.StorageRoot)) {
        throw 'Private publication requires App.StorageRoot.'
    }

    $builder = New-Object Data.Common.DbConnectionStringBuilder
    try { $builder.set_ConnectionString([string]$Defaults.App.ConnectionString) }
    catch { throw 'Private publication requires a valid App.ConnectionString.' }
    $databaseUser = $null
    foreach ($key in @('User ID', 'UserID', 'User', 'UID')) {
        if ($builder.ContainsKey($key)) { $databaseUser = [string]$builder[$key]; break }
    }
    if ([string]::IsNullOrWhiteSpace($databaseUser)) {
        throw 'Private publication requires an explicit database user.'
    }

    $secureOrigin = $origin.Scheme -eq 'https' -and $Defaults.App.CookieSecure -eq $true
    if (!$AllowInsecure -and (!$secureOrigin -or $databaseUser.Equals('root', [StringComparison]::OrdinalIgnoreCase))) {
        throw 'Private publication requires HTTPS, CookieSecure=true and a non-root database user. Use -AllowInsecurePrivateConfiguration only for an explicitly accepted isolated environment.'
    }
    if ($origin.Scheme -notin @('http', 'https')) {
        throw 'Private publication WebBaseUrl must use HTTP or HTTPS.'
    }
    if ($origin.Scheme -eq 'https' -and !$Defaults.App.CookieSecure) {
        throw 'HTTPS publication requires CookieSecure=true.'
    }
    if ($origin.Scheme -eq 'http' -and $Defaults.App.CookieSecure) {
        throw 'HTTP publication requires CookieSecure=false for login cookies.'
    }
}

function Get-PayloadFiles([string]$Root, [string[]]$ExcludedNames) {
    $items = @(Get-ChildItem -LiteralPath $Root -Recurse -Force -File |
        Where-Object { $ExcludedNames -notcontains $_.Name } |
        Sort-Object FullName)
    $result = @()
    foreach ($item in $items) {
        $relative = $item.FullName.Substring($Root.Length).TrimStart('\', '/').Replace('\', '/')
        $result += [ordered]@{
            path = $relative
            sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            bytes = [long]$item.Length
        }
    }
    return $result
}

function New-NormalizedZip([string]$SourceRoot, [string]$DestinationPath) {
    Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue

    $baseName = Split-Path -Leaf $SourceRoot
    $archive = [IO.Compression.ZipFile]::Open($DestinationPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in @(Get-ChildItem -LiteralPath $SourceRoot -Recurse -Force -File | Sort-Object FullName)) {
            $relative = $file.FullName.Substring($SourceRoot.Length).TrimStart('\', '/').Replace('\', '/')
            $entryName = $baseName + '/' + $relative
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $file.FullName,
                $entryName,
                [IO.Compression.CompressionLevel]::Optimal
            ) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\', '/')
$serverRoot = Join-Path $repoRoot 'server_dotnet'
$apiProject = Join-Path $repoRoot 'server_dotnet\Yf.Api\Yf.Api.csproj'
$globalJsonPath = Join-Path $serverRoot 'global.json'
$webRoot = Join-Path $repoRoot 'web'
$webLock = Join-Path $webRoot 'package-lock.json'
$webModules = Join-Path $webRoot 'node_modules'
$webDist = Join-Path $webRoot 'dist'
$webCompressionScript = Join-Path $webRoot 'scripts\precompress-assets.mjs'
$webPrecompressionManifest = Join-Path $webDist '.precompressed-assets.json'
$deployRoot = Join-Path $repoRoot 'server_dotnet\deploy'
$publishDefaultsPath = Join-Path $deployRoot 'publish-defaults.local.json'
$noticesSource = Join-Path $repoRoot 'server_dotnet\THIRD-PARTY-NOTICES.md'
$licensesSource = Join-Path $repoRoot 'server_dotnet\licenses'
$artifactsRoot = Join-Path $repoRoot '.artifacts'
$releasesRoot = Join-Path $repoRoot 'deloy'

$gitHead = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or !$gitHead) { throw 'Unable to resolve the Git HEAD.' }
$gitStatus = @(& git -C $repoRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) { throw 'Unable to read the Git working-tree status.' }
$isDirty = $gitStatus.Count -gt 0
if ($isDirty -and !$AllowDirty) {
    throw 'The working tree is dirty. Commit or stash the intended release inputs, or use -AllowDirty to record an explicitly accepted dirty build.'
}
if ($AllowInsecurePrivateConfiguration -and $ExternalConfigurationTemplate) {
    throw '-AllowInsecurePrivateConfiguration cannot be combined with -ExternalConfigurationTemplate.'
}
if ([string]::IsNullOrWhiteSpace($FreshOutputDirectory)) {
    $releaseId = 'Yf.System-{0}-{1}-{2}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), $gitHead.Substring(0, 8), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
    $FreshOutputDirectory = Join-Path $releasesRoot $releaseId
}
$outputRoot = Get-FullLocalPath $FreshOutputDirectory 'FreshOutputDirectory'
$outputParent = Split-Path -Parent $outputRoot
$artifactName = Split-Path -Leaf $outputRoot
$zipPath = Join-Path $outputParent ($artifactName + '.zip')
$releaseManifestPath = Join-Path $outputParent ($artifactName + '.release-manifest.json')
$zipHashPath = $zipPath + '.sha256'

Assert-NoReparsePoint $outputRoot
if (!(Test-Within $outputRoot $releasesRoot) -or $outputRoot.Equals($releasesRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "FreshOutputDirectory must be a child directory below the project artifact root: $releasesRoot"
}
if ((Test-Path -LiteralPath $outputRoot) -and !(Test-Path -LiteralPath $outputRoot -PathType Container)) {
    throw 'FreshOutputDirectory points to a file.'
}
if ((Test-Path -LiteralPath $outputRoot) -and @(Get-ChildItem -LiteralPath $outputRoot -Force).Count) {
    throw 'FreshOutputDirectory must be absent or empty; an existing release is never overwritten or cleared.'
}
foreach ($sidecar in $(if ($CreateArchive) { @($zipPath, $releaseManifestPath, $zipHashPath) } else { @() })) {
    if (Test-Path -LiteralPath $sidecar) {
        throw "Release sidecar already exists and will not be overwritten: $sidecar"
    }
}
foreach ($required in @(
    $apiProject,
    $globalJsonPath,
    $webLock,
    $webCompressionScript,
    (Join-Path $deployRoot 'install-iis.ps1'),
    (Join-Path $deployRoot 'maintain-iis.ps1'),
    (Join-Path $deployRoot 'maintenance-common.ps1'),
    (Join-Path $deployRoot 'README.md'),
    (Join-Path $deployRoot 'appsettings.example.json'),
    $noticesSource
)) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required release input is missing: $required"
    }
}
if (!(Test-Path -LiteralPath $licensesSource -PathType Container) -or
    !@(Get-ChildItem -LiteralPath $licensesSource -Force -File).Count) {
    throw 'The third-party license source directory is missing or empty.'
}

$globalJson = Get-Content -LiteralPath $globalJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
$pinnedDotnetSdk = [string]$globalJson.sdk.version
if ($pinnedDotnetSdk -notmatch '^8\.') {
    throw "server_dotnet/global.json must select a .NET 8 SDK; found: $pinnedDotnetSdk"
}
Push-Location $serverRoot
try {
    $selectedDotnetSdk = (@(& dotnet --version) -join "`n").Trim()
    $dotnetVersionExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}
if ($dotnetVersionExitCode -ne 0 -or $selectedDotnetSdk -notmatch '^8\.') {
    throw "The publishing process must select a .NET 8 SDK from server_dotnet/global.json; selected: $selectedDotnetSdk"
}

$publishDefaults = $null
if (!$ExternalConfigurationTemplate) {
    # Generate the project-specific JWT once and reuse it across releases. The
    # bootstrap password is generated separately for every private package.
    $publishDefaults = Initialize-PublishDefaults $publishDefaultsPath
    Assert-PrivatePublishDefaults $publishDefaults -AllowInsecure:$AllowInsecurePrivateConfiguration
}

if (!(Test-Path -LiteralPath $outputParent -PathType Container)) {
    New-Item -ItemType Directory -Path $outputParent | Out-Null
}
if (!(Test-Path -LiteralPath $outputRoot -PathType Container)) {
    New-Item -ItemType Directory -Path $outputRoot | Out-Null
}
Protect-ReleasePath $outputRoot

$buildCommands = @()
$frontendDependencyInstall = 'npm-ci'
$buildCommands += [ordered]@{ workingDirectory = 'web'; executable = 'npm'; arguments = @('ci') }
Push-Location $webRoot
try { Invoke-Native 'npm' @('ci') } finally { Pop-Location }
$buildCommands += [ordered]@{ workingDirectory = 'web'; executable = 'npm'; arguments = @('run', 'build') }
Push-Location $webRoot
try { Invoke-Native 'npm' @('run', 'build') } finally { Pop-Location }
if (!(Test-Path -LiteralPath (Join-Path $webDist 'index.html') -PathType Leaf)) {
    throw 'Frontend build did not produce dist/index.html.'
}
if (!(Test-Path -LiteralPath $webPrecompressionManifest -PathType Leaf)) {
    throw 'Frontend build did not produce the precompressed asset manifest.'
}

$publishArguments = @(
    'publish', $apiProject,
    '--configuration', 'Release',
    '--framework', 'net8.0',
    '--runtime', 'win-x64',
    '--no-self-contained',
    '-p:RestoreLockedMode=true',
    '--output', $outputRoot
)
$buildCommands += [ordered]@{ workingDirectory = 'server_dotnet'; executable = 'dotnet'; arguments = $publishArguments }
Push-Location $serverRoot
try { Invoke-Native 'dotnet' $publishArguments } finally { Pop-Location }

$wwwRoot = Join-Path $outputRoot 'wwwroot'
if (Test-Path -LiteralPath $wwwRoot) {
    if (!(Test-Path -LiteralPath $wwwRoot -PathType Container)) {
        throw 'Published wwwroot is not a directory.'
    }
    if (@(Get-ChildItem -LiteralPath $wwwRoot -Force).Count) {
        throw 'dotnet publish unexpectedly produced a nonempty wwwroot; refusing to mix web payloads.'
    }
} else {
    New-Item -ItemType Directory -Path $wwwRoot | Out-Null
}
foreach ($item in Get-ChildItem -LiteralPath $webDist -Force) {
    Copy-Item -LiteralPath $item.FullName -Destination $wwwRoot -Recurse
}
$packagedPrecompressionManifest = Join-Path $outputRoot 'precompressed-assets.json'
Move-Item -LiteralPath (Join-Path $wwwRoot '.precompressed-assets.json') -Destination $packagedPrecompressionManifest
$precompressionVerificationArguments = @($webCompressionScript, '--verify', $wwwRoot, $packagedPrecompressionManifest)
$buildCommands += [ordered]@{ workingDirectory = 'web'; executable = 'node'; arguments = $precompressionVerificationArguments }
Push-Location $webRoot
try { Invoke-Native 'node' $precompressionVerificationArguments } finally { Pop-Location }

foreach ($name in @('install-iis.ps1', 'maintain-iis.ps1', 'maintenance-common.ps1', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $deployRoot $name) -Destination (Join-Path $outputRoot $name)
}
$productionPath = Join-Path $outputRoot 'appsettings.Production.json'
$productionSettings = Get-Content -LiteralPath (Join-Path $deployRoot 'appsettings.example.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$configurationMode = 'bundled-private'
$insecureCookiesOverride = $false
if (!$ExternalConfigurationTemplate) {
    $productionSettings.App.ConnectionString = $publishDefaults.App.ConnectionString
    $productionSettings.App.JwtSecret = $publishDefaults.App.JwtSecret
    $productionSettings.App.BootstrapPassword = New-RandomBase64 12
    foreach ($name in @('StorageRoot', 'WebBaseUrl', 'OemStorageRoot')) {
        $override = $publishDefaults.App.PSObject.Properties[$name]
        if ($override -and ![string]::IsNullOrWhiteSpace([string]$override.Value)) {
            $productionSettings.App.$name = [string]$override.Value
        }
    }
    if ($publishDefaults.App.PSObject.Properties['CookieSecure']) {
        $productionSettings.App.CookieSecure = [bool]$publishDefaults.App.CookieSecure
    }
    # The backend accepts CookieSecure=false outside Development only for a loopback
    # WebBaseUrl or with an explicit App.AllowInsecureCookies opt-in. A private HTTP
    # package (only reachable via -AllowInsecurePrivateConfiguration) must carry that
    # opt-in or the site refuses to start; it is recorded in the manifest.
    $packagedOrigin = $null
    $needsInsecureCookies = !$productionSettings.App.CookieSecure -and
        [Uri]::TryCreate([string]$productionSettings.App.WebBaseUrl, [UriKind]::Absolute, [ref]$packagedOrigin) -and
        $packagedOrigin.Scheme -eq 'http' -and !$packagedOrigin.IsLoopback
    if ($needsInsecureCookies -and !$AllowInsecurePrivateConfiguration) {
        throw 'Insecure cookies for a non-loopback HTTP origin require -AllowInsecurePrivateConfiguration.'
    }
    $insecureCookiesOverride = [bool]$needsInsecureCookies
} else {
    $configurationMode = 'external-template'
    $productionSettings.App.ConnectionString = ''
    $productionSettings.App.JwtSecret = ''
    $productionSettings.App.BootstrapPassword = ''
}
$productionSettings.App | Add-Member -NotePropertyName AllowInsecureCookies -NotePropertyValue $insecureCookiesOverride -Force
$storageRoot = Get-FullLocalPath ([string]$productionSettings.App.StorageRoot) 'App.StorageRoot'
if (![string]::IsNullOrWhiteSpace([string]$productionSettings.App.OemStorageRoot)) {
    $oemStorageRoot = Get-FullLocalPath ([string]$productionSettings.App.OemStorageRoot) 'App.OemStorageRoot'
    if ((Test-Within $oemStorageRoot $storageRoot) -or (Test-Within $storageRoot $oemStorageRoot)) {
        throw 'App.OemStorageRoot must be independent from App.StorageRoot.'
    }
}
Write-Utf8NoBom $productionPath (($productionSettings | ConvertTo-Json -Depth 8) + "`n")
Copy-Item -LiteralPath $noticesSource -Destination (Join-Path $outputRoot 'THIRD-PARTY-NOTICES.md')
Copy-Item -LiteralPath $licensesSource -Destination (Join-Path $outputRoot 'licenses') -Recurse

# Capture license texts for every locally installed package represented in the
# npm lock. Platform-specific optional packages that npm did not install remain
# in the index with installed=false; no license or source is downloaded.
$nodeLockReader = @'
const fs = require('fs');
const lock = JSON.parse(fs.readFileSync(process.argv[1], 'utf8'));
const rows = Object.entries(lock.packages || {})
  .filter(([lockPath]) => lockPath.startsWith('node_modules/'))
  .map(([lockPath, metadata]) => {
    const marker = 'node_modules/';
    const tail = lockPath.substring(lockPath.lastIndexOf(marker) + marker.length);
    const segments = tail.split('/');
    const name = segments[0].startsWith('@') ? `${segments[0]}/${segments[1]}` : segments[0];
    return {
      lockPath,
      name,
      version: metadata.version,
      dev: metadata.dev === true,
      optional: metadata.optional === true,
      devOptional: metadata.devOptional === true
    };
  })
  .sort((a, b) => a.lockPath.localeCompare(b.lockPath));
process.stdout.write(JSON.stringify(rows));
'@
$frontendPackagesJson = @(& node -e $nodeLockReader $webLock) -join "`n"
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($frontendPackagesJson)) {
    throw 'Unable to read frontend dependency metadata from package-lock.json.'
}
# Windows PowerShell 5.1 emits a JSON array as one pipeline object. Assign it
# directly so foreach visits packages instead of treating the array as a package.
$frontendPackages = $frontendPackagesJson | ConvertFrom-Json
$frontendLicensesRoot = Join-Path $outputRoot 'licenses\frontend'
New-Item -ItemType Directory -Path $frontendLicensesRoot | Out-Null
$frontendIndexEntries = @()
foreach ($package in $frontendPackages) {
    $moduleDirectory = [IO.Path]::GetFullPath((Join-Path $webRoot $package.lockPath.Replace('/', '\')))
    if (!(Test-Within $moduleDirectory $webModules)) {
        throw "Invalid node_modules path in package lock: $($package.lockPath)"
    }
    $installed = Test-Path -LiteralPath $moduleDirectory -PathType Container
    if (!$installed -and !$package.optional) {
        throw "A non-optional locked frontend package is missing from node_modules: $($package.lockPath)"
    }

    $licenseEntries = @()
    if ($installed) {
        if ((Get-Item -LiteralPath $moduleDirectory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Linked frontend package directories are not accepted for release provenance: $($package.lockPath)"
        }
        $modulePackageJson = Join-Path $moduleDirectory 'package.json'
        if (!(Test-Path -LiteralPath $modulePackageJson -PathType Leaf)) {
            throw "Installed frontend package lacks package.json: $($package.lockPath)"
        }
        $installedMetadata = Get-Content -LiteralPath $modulePackageJson -Raw | ConvertFrom-Json
        if ([string]$installedMetadata.version -ne [string]$package.version) {
            throw "Frontend package version differs from package-lock.json: $($package.lockPath)"
        }

        $packageLicenseRoot = Join-Path $frontendLicensesRoot $package.name.Replace('/', '\')
        $packageLicenseRoot = Join-Path $packageLicenseRoot ([string]$package.version)
        foreach ($licenseFile in @(Get-ChildItem -LiteralPath $moduleDirectory -Force -File |
            Where-Object { $_.Name -match '^(?i:LICENSE|NOTICE|COPYING)' } |
            Sort-Object Name)) {
            if ($licenseFile.Length -gt 5MB) {
                throw "Frontend license text exceeds the 5 MiB safety limit: $($licenseFile.FullName)"
            }
            $licenseBytes = [IO.File]::ReadAllBytes($licenseFile.FullName)
            if ([Array]::IndexOf($licenseBytes, [byte]0) -ge 0) {
                throw "Frontend license candidate is not a text file: $($licenseFile.FullName)"
            }
            if (!(Test-Path -LiteralPath $packageLicenseRoot -PathType Container)) {
                New-Item -ItemType Directory -Path $packageLicenseRoot -Force | Out-Null
            }
            $destination = Join-Path $packageLicenseRoot $licenseFile.Name
            if (Test-Path -LiteralPath $destination -PathType Leaf) {
                if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne
                    (Get-FileHash -LiteralPath $licenseFile.FullName -Algorithm SHA256).Hash) {
                    throw "Conflicting license texts for package and version: $($package.name) $($package.version)"
                }
            } else {
                Copy-Item -LiteralPath $licenseFile.FullName -Destination $destination
            }
            $relativeLicense = $destination.Substring($outputRoot.Length).TrimStart('\', '/').Replace('\', '/')
            $licenseEntries += [ordered]@{
                path = $relativeLicense
                sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
                bytes = [long](Get-Item -LiteralPath $destination).Length
            }
        }
    }

    $dependencyClass = if ($package.dev) { 'development' } else { 'production' }
    $frontendIndexEntries += [ordered]@{
        package = [string]$package.name
        version = [string]$package.version
        lockPath = [string]$package.lockPath
        dependencyClass = $dependencyClass
        optional = [bool]$package.optional
        devOptional = [bool]$package.devOptional
        installed = [bool]$installed
        licenseFiles = $licenseEntries
    }
}
$frontendIndex = [ordered]@{
    schemaVersion = 1
    source = 'web/package-lock.json'
    sourceSha256 = (Get-FileHash -LiteralPath $webLock -Algorithm SHA256).Hash.ToLowerInvariant()
    scope = 'All locked frontend packages; dependencyClass reflects the lock dev flag and does not assert bundle inclusion.'
    packages = $frontendIndexEntries
}
Write-Utf8NoBom (Join-Path $frontendLicensesRoot 'INDEX.json') (($frontendIndex | ConvertTo-Json -Depth 8) + "`n")
$frontendLicenseReadme = @"
# Frontend dependency licenses

This directory was generated from ``web/package-lock.json`` and the matching local
``web/node_modules`` tree. It contains only root-level ``LICENSE*``, ``NOTICE*`` and
``COPYING*`` text files. ``INDEX.json`` records every locked package, version, lock
path, production/development flag, optional status, local installation status and
copied file hash. Inclusion in this lock-derived list does not mean every package is
present in the production JavaScript bundle.
"@
Write-Utf8NoBom (Join-Path $frontendLicensesRoot 'README.md') ($frontendLicenseReadme.Trim() + "`n")

# Keep environment-specific values only in appsettings.Production.json.
# IIS explicitly selects Production; the base file contains non-secret shared settings.
$activeSettings = [ordered]@{
    Logging = [ordered]@{ LogLevel = [ordered]@{ Default = 'Information'; 'Microsoft.AspNetCore' = 'Warning' } }
    AllowedHosts = '*'
}
Write-Utf8NoBom (Join-Path $outputRoot 'appsettings.json') (($activeSettings | ConvertTo-Json -Depth 8) + "`n")

# Use the launch form required by the IIS installation and maintenance scripts.
$webConfigPath = Join-Path $outputRoot 'web.config'
[xml]$webConfig = Get-Content -LiteralPath $webConfigPath -Raw -Encoding UTF8
$aspNetCore = $webConfig.SelectSingleNode('//aspNetCore')
if (!$aspNetCore) { throw 'Published IIS configuration is missing aspNetCore.' }
$aspNetCore.SetAttribute('processPath', 'dotnet')
$aspNetCore.SetAttribute('arguments', '.\Yf.Api.dll')
$environmentVariables = $aspNetCore.SelectSingleNode('environmentVariables')
if (!$environmentVariables) {
    $environmentVariables = $webConfig.CreateElement('environmentVariables')
    [void]$aspNetCore.AppendChild($environmentVariables)
}
foreach ($name in @('ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT')) {
    $variable = $environmentVariables.SelectSingleNode("environmentVariable[@name='$name']")
    if (!$variable) {
        $variable = $webConfig.CreateElement('environmentVariable')
        $variable.SetAttribute('name', $name)
        [void]$environmentVariables.AppendChild($variable)
    }
    $variable.SetAttribute('value', 'Production')
}
$webConfig.Save($webConfigPath)

$forbiddenNames = @('secrets.json', '.env')
$forbiddenExtensions = @('.pfx', '.p12', '.key')
foreach ($file in @(Get-ChildItem -LiteralPath $outputRoot -Recurse -Force -File)) {
    if (($forbiddenNames -contains $file.Name) -or ($forbiddenExtensions -contains $file.Extension.ToLowerInvariant()) -or $file.Name.StartsWith('.env.')) {
        throw "Forbidden local or secret-bearing file in release payload: $($file.FullName)"
    }
}
$allowedAppSettings = @('appsettings.json', 'appsettings.Production.json')
foreach ($file in @(Get-ChildItem -LiteralPath $outputRoot -Recurse -Force -File -Filter 'appsettings*.json')) {
    $relative = $file.FullName.Substring($outputRoot.Length).TrimStart('\', '/').Replace('\', '/')
    if ($allowedAppSettings -notcontains $relative) {
        throw "Unexpected appsettings JSON in release payload: $relative"
    }
}
$packagedSettings = Get-Content -LiteralPath $productionPath -Raw | ConvertFrom-Json
if ($packagedSettings.App.ConnectionString -ne $productionSettings.App.ConnectionString -or
    $packagedSettings.App.JwtSecret -ne $productionSettings.App.JwtSecret -or
    $packagedSettings.App.BootstrapPassword -ne $productionSettings.App.BootstrapPassword -or
    $packagedSettings.App.StorageRoot -ne $productionSettings.App.StorageRoot -or
    $packagedSettings.App.WebBaseUrl -ne $productionSettings.App.WebBaseUrl -or
    $packagedSettings.App.OemStorageRoot -ne $productionSettings.App.OemStorageRoot) {
    throw 'Packaged appsettings.Production.json does not match the selected configuration mode.'
}
foreach ($requiredPayload in @(
    'Yf.Api.dll', 'Yf.Api.runtimeconfig.json', 'web.config', 'wwwroot\index.html', 'precompressed-assets.json',
    'install-iis.ps1',
    'maintain-iis.ps1', 'maintenance-common.ps1'
)) {
    if (!(Test-Path -LiteralPath (Join-Path $outputRoot $requiredPayload) -PathType Leaf)) {
        throw "Required published payload is missing: $requiredPayload"
    }
}

$createdUtc = [DateTime]::UtcNow.ToString('o')
$payloadFiles = @(Get-PayloadFiles $outputRoot @('manifest.json'))
$packageManifest = [ordered]@{
    schemaVersion = 1
    artifact = $artifactName
    createdUtc = $createdUtc
    source = [ordered]@{
        gitHead = $gitHead
        dirty = $isDirty
        changes = @($gitStatus)
    }
    build = [ordered]@{
        configuration = 'Release'
        targetFramework = 'net8.0'
        runtimeIdentifier = 'win-x64'
        selfContained = $false
        sdkVersion = $selectedDotnetSdk
        globalJsonSdkVersion = $pinnedDotnetSdk
        frontendDependencyInstall = $frontendDependencyInstall
        commands = $buildCommands
    }
    configuration = [ordered]@{
        mode = $configurationMode
        containsSecrets = !$ExternalConfigurationTemplate
        insecureOverride = [bool]$AllowInsecurePrivateConfiguration
        insecureCookies = [bool]$insecureCookiesOverride
    }
    oem = [ordered]@{
        storageConfigured = ![string]::IsNullOrWhiteSpace([string]$productionSettings.App.OemStorageRoot)
    }
    files = $payloadFiles
}
$packageManifestPath = Join-Path $outputRoot 'manifest.json'
Write-Utf8NoBom $packageManifestPath (($packageManifest | ConvertTo-Json -Depth 8) + "`n")

Write-Host "Package root: $outputRoot"
if (!$CreateArchive) {
    Write-Host 'Deployment folder ready. Copy the whole folder to the IIS server.'
    return
}

New-NormalizedZip $outputRoot $zipPath
Protect-ReleasePath $zipPath -File
$zipInfo = Get-Item -LiteralPath $zipPath
$zipSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$allPackagedFiles = @(Get-PayloadFiles $outputRoot @())
$releaseManifest = [ordered]@{
    schemaVersion = 1
    artifact = $artifactName
    createdUtc = $createdUtc
    source = $packageManifest.source
    build = $packageManifest.build
    configuration = $packageManifest.configuration
    oem = $packageManifest.oem
    files = $allPackagedFiles
    archive = [ordered]@{
        path = $zipInfo.Name
        sha256 = $zipSha256
        bytes = [long]$zipInfo.Length
    }
}
Write-Utf8NoBom $releaseManifestPath (($releaseManifest | ConvertTo-Json -Depth 8) + "`n")
Write-Utf8NoBom $zipHashPath ($zipSha256 + '  ' + $zipInfo.Name + "`n")
Protect-ReleasePath $releaseManifestPath -File
Protect-ReleasePath $zipHashPath -File

Write-Host "Archive: $zipPath"
Write-Host "Release manifest: $releaseManifestPath"
Write-Host "Archive SHA256: $zipSha256"
