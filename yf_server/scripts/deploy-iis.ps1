#Requires -Version 7.2
<#
.SYNOPSIS
  构建 yf_system 前端和 Rust 后端，生成一份配套的 Windows/IIS 发布目录。

.DESCRIPTION
  发布目录包含两个互相隔离的运行边界：
  - site：IIS 静态站点，包含前端 dist、SPA 回退和 /api 反向代理配置；
  - server：Rust 后端及迁移程序，由 WinSW/NSSM 等服务管理器托管。

  脚本只生成发布制品，不会修改 IIS、Windows 服务、数据库或业务存储。
  config.local.toml、数据库、上传文件和开发缓存不会进入发布目录。

.PARAMETER OutputDir
  发布目录。默认位于仓库已忽略的 .runlogs\iis-release。
.PARAMETER Clean
  输出目录已存在时，将旧目录改名保留后再切换到新制品。
.PARAMETER SkipInstall
  不执行 npm ci；仅在 node_modules 已按当前 package-lock.json 安装时使用。
.PARAMETER SkipChecks
  跳过前端 lint 和后端测试。生产发布通常不应使用。
.PARAMETER Offline
  npm 和 Cargo 只使用本机缓存；缓存不足时直接失败。
.PARAMETER AllowDirty
  允许从有未提交修改的工作区生成制品，并在清单中记录状态。
.PARAMETER BackendUrl
  IIS /api 反向代理目标，必须是回环 HTTP 地址。默认 http://127.0.0.1:8080。
.PARAMETER WinSWPath
  可选的 WinSW 2.12.0 x64 文件。未提供时从官方 GitHub Release 下载并校验固定 SHA-256。
.PARAMETER Plan
  只验证环境并显示计划，不安装、构建或写入输出目录。
#>

[CmdletBinding()]
param(
    [string] $OutputDir = '',
    [switch] $Clean,
    [switch] $SkipInstall,
    [switch] $SkipChecks,
    [switch] $Offline,
    [switch] $AllowDirty,
    [string] $BackendUrl = 'http://127.0.0.1:8080',
    [string] $WinSWPath = '',
    [switch] $Plan
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# 当前 web 依赖 .NET 的主项目/子项目和 SignalR 契约，不能再配套归档 Rust 后端。
# 必须在工具探测、安装依赖、构建或任何输出写入之前拒绝旧发布入口。
throw 'Rust IIS 发布入口已停用：当前前端仅配套 server_dotnet。请从仓库根目录运行 pwsh -File .\server_dotnet\scripts\publish-iis.ps1 -FreshOutputDirectory <新的空发布目录>，并使用该发布包内的 install-iis.ps1。'

function Resolve-FullPath([string] $Value) {
    return [System.IO.Path]::GetFullPath(
        $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Value)
    ).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
}

function Assert-Command([string] $Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "未找到命令：$Name"
    }
}

function Invoke-Checked([string] $Command, [string[]] $Arguments, [string] $WorkingDirectory) {
    Push-Location $WorkingDirectory
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "命令失败（退出码 $LASTEXITCODE）：$Command $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

function Invoke-CargoChecked([string[]] $Arguments, [string] $WorkingDirectory, [string] $TargetDirectory) {
    $previousTarget = $env:CARGO_TARGET_DIR
    try {
        $env:CARGO_TARGET_DIR = $TargetDirectory
        Invoke-Checked 'cargo' $Arguments $WorkingDirectory
    }
    finally {
        $env:CARGO_TARGET_DIR = $previousTarget
    }
}

function Write-Utf8NoBom([string] $PathValue, [string] $Content) {
    [System.IO.File]::WriteAllText(
        $PathValue,
        $Content.Trim() + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false)
    )
}

function Assert-SafeOutputPath([string] $PathValue, [string] $RepositoryRoot) {
    $candidate = Resolve-FullPath $PathValue
    $repo = Resolve-FullPath $RepositoryRoot
    $driveRoot = [System.IO.Path]::GetPathRoot($candidate).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    if ($candidate -notmatch '^[A-Za-z]:[\\/]' -or
        $repo.StartsWith($candidate + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $candidate.Equals($env:USERPROFILE, [StringComparison]::OrdinalIgnoreCase) -or
        $candidate.StartsWith($env:windir.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw '发布输出不能使用网络路径、仓库父目录、用户主目录或系统目录。'
    }
    $ancestor = $candidate
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw '发布输出不能经过目录联接或符号链接。'
        }
        $ancestor = Split-Path -Parent $ancestor
    }
    $protected = @(
        $repo,
        (Join-Path $repo '.git'),
        (Join-Path $repo 'web'),
        (Join-Path $repo 'yf_server')
    ) | ForEach-Object { Resolve-FullPath $_ }

    if ($candidate -eq $driveRoot -or $protected -contains $candidate) {
        throw "拒绝使用磁盘根、仓库根或源码目录作为发布输出：$candidate"
    }
    foreach ($sourceRoot in $protected | Select-Object -Skip 1) {
        if ($candidate.StartsWith($sourceRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "拒绝在源码目录内生成可清理的发布输出：$candidate"
        }
    }
    return $candidate
}

$repoRoot = Resolve-FullPath (Join-Path $PSScriptRoot '..\..')
$frontendDir = Join-Path $repoRoot 'web'
$backendDir = Join-Path $repoRoot 'yf_server'
$frontendLock = Join-Path $frontendDir 'package-lock.json'
$backendLock = Join-Path $backendDir 'Cargo.lock'
$distDir = Join-Path $frontendDir 'dist'
$buildRoot = Join-Path $repoRoot ('.runlogs\deploy-build\' + [guid]::NewGuid().ToString('N'))
$releaseDir = Join-Path $buildRoot 'release'
$targetInstaller = Join-Path $backendDir 'deploy\install-iis.ps1'
$backupScript = Join-Path $backendDir 'scripts\backup.ps1'
$backupOptions = Join-Path $backendDir 'scripts\backup-options.ps1'
$winswVersion = '2.12.0'
$winswSha256 = '05B82D46AD331CC16BDC00DE5C6332C1EF818DF8CEEFCD49C726553209B3A0DA'
$winswLicenseSha256 = '1CDF703C10A70E5973BF3ACF2A5EEABE7746237155B92DB2034AEAE26FDF7802'

trap {
    if (Test-Path -LiteralPath $buildRoot) {
        Remove-Item -LiteralPath $buildRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    throw $_
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot '.runlogs\iis-release'
}
$OutputDir = Assert-SafeOutputPath $OutputDir $repoRoot

$backendUri = $null
if (-not [System.Uri]::TryCreate($BackendUrl, [System.UriKind]::Absolute, [ref] $backendUri) -or
    $backendUri.Scheme -ne 'http' -or
    $backendUri.AbsolutePath -ne '/' -or
    -not @('127.0.0.1').Contains($backendUri.Host)) {
    throw 'BackendUrl 必须是没有路径、查询或凭据的回环 HTTP 地址，例如 http://127.0.0.1:8080'
}
if (-not [string]::IsNullOrEmpty($backendUri.UserInfo) -or -not [string]::IsNullOrEmpty($backendUri.Query) -or -not [string]::IsNullOrEmpty($backendUri.Fragment)) {
    throw 'BackendUrl 不能包含凭据或查询参数。'
}
$BackendUrl = $BackendUrl.TrimEnd('/')

Assert-Command 'node'
Assert-Command 'npm'
Assert-Command 'cargo'
Assert-Command 'rustc'
Assert-Command 'git'

foreach ($required in @(
    (Join-Path $frontendDir 'package.json'),
    $frontendLock,
    (Join-Path $backendDir 'Cargo.toml'),
    $backendLock,
    $targetInstaller,
    (Join-Path $backendDir 'deploy\README-iis.md'),
    $backupScript,
    $backupOptions
)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "缺少构建输入：$required"
    }
}

$nodeVersion = (& node --version | Out-String).Trim()
$npmVersion = (& npm --version | Out-String).Trim()
$cargoVersion = (& cargo --version | Out-String).Trim()
$rustcVersion = (& rustc --version | Out-String).Trim()
$rustcVerbose = (& rustc -vV | Out-String)
if ($rustcVerbose -notmatch '(?m)^host: x86_64-pc-windows-msvc\s*$') {
    throw '当前脚本只生成 Windows x64 MSVC 后端，请使用 x86_64-pc-windows-msvc Rust 工具链。'
}
$nodeMatch = [regex]::Match($nodeVersion, '^v(?<major>\d+)\.(?<minor>\d+)\.')
if (-not $nodeMatch.Success) { throw "无法识别 Node.js 版本：$nodeVersion" }
$nodeMajor = [int]$nodeMatch.Groups['major'].Value
$nodeMinor = [int]$nodeMatch.Groups['minor'].Value
if ($nodeMajor -lt 22 -or ($nodeMajor -eq 22 -and $nodeMinor -lt 13) -or $nodeMajor -eq 23) {
    throw "Node.js 版本不满足项目要求（22.13+ 或 24+）：$nodeVersion"
}

$gitRoot = (& git -C $repoRoot rev-parse --show-toplevel | Out-String).Trim()
if ((Resolve-FullPath $gitRoot) -ne $repoRoot) { throw "Git 根目录与项目不一致：$gitRoot" }
$revision = (& git -C $repoRoot rev-parse HEAD | Out-String).Trim()
$branch = (& git -C $repoRoot branch --show-current | Out-String).Trim()
$worktreeStatus = @(& git -C $repoRoot status --porcelain=v1)
$worktreeClean = $worktreeStatus.Count -eq 0

$planData = [ordered]@{
    repository = $repoRoot
    branch = $branch
    revision = $revision
    worktreeClean = $worktreeClean
    output = $OutputDir
    zip = "$OutputDir.zip"
    site = Join-Path $OutputDir 'site'
    server = Join-Path $OutputDir 'server'
    backendProxy = $BackendUrl
    targetInstaller = $targetInstaller
    serviceWrapper = if ($WinSWPath) { Resolve-FullPath $WinSWPath } else { "WinSW $winswVersion 官方发布文件（固定 SHA-256）" }
    node = $nodeVersion
    npm = $npmVersion
    cargo = $cargoVersion
    rustc = $rustcVersion
    install = if ($SkipInstall) { '跳过' } elseif ($Offline) { 'npm ci --offline' } else { 'npm ci' }
    checks = if ($SkipChecks) { '跳过' } else { 'npm run lint；cargo test -p server --bin server' }
    build = "npm run build；隔离 CARGO_TARGET_DIR=$buildRoot；cargo build -p server -p migration --release -j 1"
}

Write-Host '==> 前后端 IIS 发布计划'
$planData | Format-List | Out-Host
if ($Plan) {
    if ($WinSWPath) {
        $candidateWinSW = Resolve-FullPath $WinSWPath
        if (-not (Test-Path -LiteralPath $candidateWinSW -PathType Leaf)) { throw "WinSWPath 不存在：$candidateWinSW" }
        $candidateHash = (Get-FileHash -LiteralPath $candidateWinSW -Algorithm SHA256).Hash
        if ($candidateHash -ne $winswSha256) { throw "WinSWPath 不是已固定的 WinSW $winswVersion x64 文件。" }
    }
    Write-Host '==> Plan 模式完成：未安装依赖、未构建、未写入发布目录。'
    return
}
if (-not $worktreeClean -and -not $AllowDirty) {
    throw '工作区存在未提交修改。请先提交，或明确使用 -AllowDirty 并在发布清单中保留状态。'
}
$zipPath = "$OutputDir.zip"
$zipHashPath = "$zipPath.sha256"
if (((Test-Path -LiteralPath $OutputDir) -or (Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $zipHashPath)) -and -not $Clean) {
    throw "发布目录或 ZIP 已存在：$OutputDir。核对后使用 -Clean 保留旧制品并生成新制品。"
}

$toolCache = Join-Path $repoRoot '.runlogs\tool-cache'
$resolvedWinSW = if ($WinSWPath) { Resolve-FullPath $WinSWPath } else { Join-Path $toolCache "WinSW-x64-$winswVersion.exe" }
$resolvedWinSWLicense = Join-Path $toolCache "WinSW-LICENSE-$winswVersion.txt"
if (-not (Test-Path -LiteralPath $resolvedWinSW -PathType Leaf)) {
    if ($Offline) { throw "离线模式缺少 WinSW：$resolvedWinSW。请使用 -WinSWPath 提供固定版本文件。" }
    New-Item -ItemType Directory -Path $toolCache -Force | Out-Null
    Write-Host "==> 从 WinSW 官方 GitHub Release 获取 $winswVersion x64"
    Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/winsw/winsw/releases/download/v$winswVersion/WinSW-x64.exe" -OutFile $resolvedWinSW
}
if ((Get-FileHash -LiteralPath $resolvedWinSW -Algorithm SHA256).Hash -ne $winswSha256) {
    throw "WinSW $winswVersion SHA-256 不一致，拒绝打包。"
}
if (-not (Test-Path -LiteralPath $resolvedWinSWLicense -PathType Leaf)) {
    if ($Offline) { throw "离线模式缺少 WinSW 许可证：$resolvedWinSWLicense。" }
    New-Item -ItemType Directory -Path $toolCache -Force | Out-Null
    Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/winsw/winsw/v$winswVersion/LICENSE.txt" -OutFile $resolvedWinSWLicense
}
if ((Get-FileHash -LiteralPath $resolvedWinSWLicense -Algorithm SHA256).Hash -ne $winswLicenseSha256) {
    throw "WinSW $winswVersion 许可证 SHA-256 不一致，拒绝打包。"
}

$npmInstallArgs = @('ci')
$cargoCommonArgs = @('--locked')
if ($Offline) {
    $npmInstallArgs += '--offline'
    $cargoCommonArgs += '--offline'
}

if (-not $SkipInstall) {
    Write-Host '==> 按 package-lock.json 安装前端依赖'
    Invoke-Checked 'npm' $npmInstallArgs $frontendDir
}
elseif (-not (Test-Path -LiteralPath (Join-Path $frontendDir 'node_modules') -PathType Container)) {
    throw '-SkipInstall 需要已经存在 web/node_modules。'
}

if (-not $SkipChecks) {
    Write-Host '==> 执行前端 lint'
    Invoke-Checked 'npm' @('run', 'lint') $frontendDir
    Write-Host '==> 执行后端测试'
    Invoke-CargoChecked (@('test') + $cargoCommonArgs + @('-j', '1', '-p', 'server', '--bin', 'server')) $backendDir $buildRoot
}

Write-Host '==> 构建前端生产文件'
Invoke-Checked 'npm' @('run', 'build') $frontendDir
if (-not (Test-Path -LiteralPath (Join-Path $distDir 'index.html') -PathType Leaf)) {
    throw '前端构建产物缺少 dist/index.html。'
}

Write-Host '==> 构建 Rust Release 后端和迁移程序'
Invoke-CargoChecked (@('build') + $cargoCommonArgs + @('--release', '-j', '1', '-p', 'server', '-p', 'migration')) $backendDir $buildRoot
$serverExe = Join-Path $releaseDir 'server.exe'
$migrationExe = Join-Path $releaseDir 'migration.exe'
foreach ($required in @($serverExe, $migrationExe)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "后端构建产物缺失：$required"
    }
}

$outputParent = Split-Path -Parent $OutputDir
New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
$stagingDir = "$OutputDir.staging.$([guid]::NewGuid().ToString('N'))"
$stagingZip = "$OutputDir.staging.$([guid]::NewGuid().ToString('N')).zip"
$previousDir = $null
$previousZip = $null
$previousZipHash = $null

try {
    New-Item -ItemType Directory -Path $stagingDir | Out-Null
    $siteDir = Join-Path $stagingDir 'site'
    $serverDir = Join-Path $stagingDir 'server'
    $toolsDir = Join-Path $stagingDir 'tools'
    $scriptsDir = Join-Path $stagingDir 'scripts'
    New-Item -ItemType Directory -Path $siteDir, $serverDir, $toolsDir, $scriptsDir | Out-Null

    Get-ChildItem -LiteralPath $distDir -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $siteDir -Recurse -Force
    }
    Copy-Item -LiteralPath $serverExe, $migrationExe -Destination $serverDir
    Copy-Item -LiteralPath $targetInstaller -Destination (Join-Path $stagingDir 'install-iis.ps1')
    Copy-Item -LiteralPath $backupScript, $backupOptions -Destination $scriptsDir
    Copy-Item -LiteralPath $resolvedWinSW -Destination (Join-Path $toolsDir 'WinSW-x64.exe')
    Copy-Item -LiteralPath $resolvedWinSWLicense -Destination (Join-Path $toolsDir 'WinSW-LICENSE.txt')

    $webConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <rewrite>
      <rules>
        <rule name="Reverse proxy API" stopProcessing="true">
          <match url="^api/(.*)" />
          <serverVariables>
            <set name="HTTP_X_FORWARDED_FOR" value="{REMOTE_ADDR}" />
          </serverVariables>
          <action type="Rewrite" url="$BackendUrl/api/{R:1}" appendQueryString="true" />
        </rule>
        <rule name="SPA fallback" stopProcessing="true">
          <match url=".*" />
          <conditions logicalGrouping="MatchAll">
            <add input="{REQUEST_FILENAME}" matchType="IsFile" negate="true" />
            <add input="{REQUEST_FILENAME}" matchType="IsDirectory" negate="true" />
            <add input="{URL}" pattern="^/api(?:/|$)" negate="true" />
          </conditions>
          <action type="Rewrite" url="/index.html" />
        </rule>
      </rules>
    </rewrite>
    <security>
      <requestFiltering>
        <requestLimits maxAllowedContentLength="67108864" />
      </requestFiltering>
    </security>
    <staticContent>
      <remove fileExtension=".mjs" />
      <mimeMap fileExtension=".mjs" mimeType="application/javascript" />
      <remove fileExtension=".bcmap" />
      <mimeMap fileExtension=".bcmap" mimeType="application/octet-stream" />
      <remove fileExtension=".pfb" />
      <mimeMap fileExtension=".pfb" mimeType="application/octet-stream" />
      <remove fileExtension=".icc" />
      <mimeMap fileExtension=".icc" mimeType="application/vnd.iccprofile" />
      <remove fileExtension=".wasm" />
      <mimeMap fileExtension=".wasm" mimeType="application/wasm" />
    </staticContent>
    <httpProtocol>
      <customHeaders>
        <remove name="X-Content-Type-Options" />
        <add name="X-Content-Type-Options" value="nosniff" />
        <remove name="X-Frame-Options" />
        <add name="X-Frame-Options" value="DENY" />
        <remove name="Referrer-Policy" />
        <add name="Referrer-Policy" value="no-referrer" />
        <remove name="Content-Security-Policy" />
        <add name="Content-Security-Policy" value="frame-ancestors 'none'; object-src 'none'; base-uri 'self'" />
      </customHeaders>
    </httpProtocol>
    <httpErrors existingResponse="PassThrough" />
  </system.webServer>
</configuration>
"@
    Write-Utf8NoBom (Join-Path $siteDir 'web.config') $webConfig

    $configExample = @'
[server]
addr = "127.0.0.1:8080"
trust_loopback_proxy = true

[database]
url = "mysql://USER:PASSWORD@DB_HOST:3306/yf_system?ssl-mode=VERIFY_IDENTITY"
auto_migrate = false

[storage]
root = "D:/yf_storage"

[jwt]
secret = "REPLACE_ME"
access_ttl_minutes = 30
refresh_ttl_days = 7
cookie_secure = true

[upload]
max_file_size = 21474836480
chunk_size = 10485760

[smtp]
host = ""
port = 465
username = ""
password = ""
from = ""

[web]
base_url = "https://replace.example.com"
'@
    $configExample = $configExample.Replace('127.0.0.1:8080', "127.0.0.1:$($backendUri.Port)")
    Write-Utf8NoBom (Join-Path $serverDir 'config.example.toml') $configExample
    # UTF-8 BOM is required for Chinese literals in Windows PowerShell 5.1.
    foreach ($scriptFile in @((Join-Path $stagingDir 'install-iis.ps1'), (Join-Path $scriptsDir 'backup.ps1'), (Join-Path $scriptsDir 'backup-options.ps1'))) {
        [IO.File]::WriteAllText($scriptFile, [IO.File]::ReadAllText($scriptFile), [Text.UTF8Encoding]::new($true))
    }

    $deploymentGuide = [IO.File]::ReadAllText((Join-Path $backendDir 'deploy\README-iis.md'))
    Write-Utf8NoBom (Join-Path $stagingDir 'DEPLOYMENT.md') $deploymentGuide

    $manifest = [ordered]@{
        project = 'yf_system'
        generatedAt = (Get-Date).ToUniversalTime().ToString('o')
        repository = $repoRoot
        branch = $branch
        revision = $revision
        worktreeClean = $worktreeClean
        worktreeStatus = $worktreeStatus
        target = 'Windows x64 / IIS static frontend / Rust service backend'
        node = $nodeVersion
        npm = $npmVersion
        cargo = $cargoVersion
        rustc = $rustcVersion
        frontendInstall = $planData.install
        checks = $planData.checks
        backendProxy = $BackendUrl
        layout = [ordered]@{ site = 'IIS physical path'; server = 'WinSW service working directory'; installer = 'install-iis.ps1' }
        targetPrerequisites = @('Windows Server x64', 'IIS management service', 'IIS URL Rewrite 2', 'IIS ARR', 'Visual C++ 2015-2022 x64 Runtime', 'HTTPS certificate')
        bundledTools = @([ordered]@{
            name = 'WinSW'
            version = $winswVersion
            repository = 'https://github.com/winsw/winsw'
            source = "https://github.com/winsw/winsw/releases/download/v$winswVersion/WinSW-x64.exe"
            sha256 = $winswSha256.ToLowerInvariant()
            provenance = 'Downloaded from official Release and pinned locally; no upstream checksum/signature verification claimed'
            license = 'MIT; tools/WinSW-LICENSE.txt'
        })
        secretsIncluded = $false
        databaseIncluded = $false
        storageIncluded = $false
    }
    Write-Utf8NoBom (Join-Path $stagingDir 'deploy-manifest.json') ($manifest | ConvertTo-Json -Depth 6)

    $forbiddenNames = @('config.local.toml', 'credentials.private.json', 'database.sql')
    $forbidden = Get-ChildItem -LiteralPath $stagingDir -File -Recurse | Where-Object { $forbiddenNames -contains $_.Name }
    if ($forbidden) { throw "发布制品包含禁止文件：$($forbidden.FullName -join ', ')" }
    foreach ($required in @(
        (Join-Path $siteDir 'index.html'),
        (Join-Path $siteDir 'web.config'),
        (Join-Path $serverDir 'server.exe'),
        (Join-Path $serverDir 'migration.exe'),
        (Join-Path $serverDir 'config.example.toml'),
        (Join-Path $stagingDir 'install-iis.ps1'),
        (Join-Path $toolsDir 'WinSW-x64.exe'),
        (Join-Path $toolsDir 'WinSW-LICENSE.txt'),
        (Join-Path $scriptsDir 'backup.ps1'),
        (Join-Path $scriptsDir 'backup-options.ps1'),
        (Join-Path $stagingDir 'DEPLOYMENT.md'),
        (Join-Path $stagingDir 'deploy-manifest.json')
    )) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "发布制品缺少：$required" }
    }

    $hashLines = Get-ChildItem -LiteralPath $stagingDir -File -Recurse |
        Where-Object Name -ne 'SHA256SUMS.txt' |
        Sort-Object FullName |
        ForEach-Object {
            $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
            $relative = [System.IO.Path]::GetRelativePath($stagingDir, $_.FullName).Replace('\', '/')
            "$($hash.Hash.ToLowerInvariant())  $relative"
        }
    Write-Utf8NoBom (Join-Path $stagingDir 'SHA256SUMS.txt') ($hashLines -join [Environment]::NewLine)

    Write-Host '==> 生成并逐项读取 ZIP，验证成员可解压'
    Compress-Archive -Path (Join-Path $stagingDir '*') -DestinationPath $stagingZip -CompressionLevel Optimal
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($stagingZip)
    try {
        $zipMembers = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
        $expectedFiles = @(Get-ChildItem -LiteralPath $stagingDir -File -Recurse -Force)
        if ($zipMembers.Count -ne $expectedFiles.Count) { throw "ZIP 成员数量与发布目录不一致：$($zipMembers.Count)" }
        $seenMembers = @{}
        foreach ($entry in $zipMembers) {
            if ([System.IO.Path]::IsPathRooted($entry.FullName) -or $entry.FullName.Replace('/', '\').Split('\') -contains '..') {
                throw "ZIP 包含越界成员：$($entry.FullName)"
            }
            $stream = $entry.Open()
            try {
                $relative = $entry.FullName.Replace('/', '\')
                if ($seenMembers.ContainsKey($relative)) { throw "ZIP 重复成员：$relative" }
                $seenMembers[$relative] = $true
                $sourceFile = Join-Path $stagingDir $relative
                if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) { throw "ZIP 存在额外成员：$relative" }
                $algorithm = [Security.Cryptography.SHA256]::Create()
                try { $memberHash = [Convert]::ToHexString($algorithm.ComputeHash($stream)) }
                finally { $algorithm.Dispose() }
                if ($memberHash -ne (Get-FileHash -LiteralPath $sourceFile).Hash) { throw "ZIP 成员哈希不一致：$relative" }
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $archive.Dispose() }

    if (Test-Path -LiteralPath $OutputDir) {
        $previousDir = "$OutputDir.previous.$(Get-Date -Format 'yyyyMMddHHmmss')"
        Move-Item -LiteralPath $OutputDir -Destination $previousDir
    }
    if (Test-Path -LiteralPath $zipPath) {
        $previousZip = "$zipPath.previous.$(Get-Date -Format 'yyyyMMddHHmmss')"
        Move-Item -LiteralPath $zipPath -Destination $previousZip
    }
    if (Test-Path -LiteralPath $zipHashPath) {
        $previousZipHash = "$zipHashPath.previous.$(Get-Date -Format 'yyyyMMddHHmmss')"
        Move-Item -LiteralPath $zipHashPath -Destination $previousZipHash
    }
    try {
        Move-Item -LiteralPath $stagingDir -Destination $OutputDir
        Move-Item -LiteralPath $stagingZip -Destination $zipPath
        $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Utf8NoBom $zipHashPath "$zipHash  $([System.IO.Path]::GetFileName($zipPath))"
    }
    catch {
        if ($previousDir -and -not (Test-Path -LiteralPath $OutputDir) -and (Test-Path -LiteralPath $previousDir)) {
            Move-Item -LiteralPath $previousDir -Destination $OutputDir
        }
        if ($previousZip -and -not (Test-Path -LiteralPath $zipPath) -and (Test-Path -LiteralPath $previousZip)) {
            Move-Item -LiteralPath $previousZip -Destination $zipPath
        }
        if ($previousZipHash -and -not (Test-Path -LiteralPath $zipHashPath) -and (Test-Path -LiteralPath $previousZipHash)) {
            Move-Item -LiteralPath $previousZipHash -Destination $zipHashPath
        }
        throw
    }

    Write-Host "==> 前后端发布目录已生成：$OutputDir"
    Write-Host "==> 可传输正式部署包：$zipPath"
    Write-Host "==> ZIP SHA-256：$zipHashPath"
    if ($previousDir) { Write-Host "==> 旧发布目录已保留：$previousDir" }
    Write-Host '==> 本脚本未修改 IIS、Windows 服务、数据库或业务存储。'
}
finally {
    if (Test-Path -LiteralPath $stagingDir) {
        Remove-Item -LiteralPath $stagingDir -Recurse -Force
    }
    if (Test-Path -LiteralPath $stagingZip) {
        Remove-Item -LiteralPath $stagingZip -Force
    }
    if (Test-Path -LiteralPath $buildRoot) {
        Remove-Item -LiteralPath $buildRoot -Recurse -Force
    }
}
