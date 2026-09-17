#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
  在目标 Windows 服务器上部署 yf_system 前端、Rust 后端服务和 IIS 站点。

.DESCRIPTION
  此脚本只能从 deploy-iis.ps1 生成的发布包根目录运行。它先完成全部只读检查，
  再停止旧站点和服务、备份程序文件，并按显式选择决定是否备份业务数据和执行迁移。
  生产配置和数据库密码不会写入发布包或命令行。

.PARAMETER HostName
  正式 HTTPS 主机名，例如 yf.example.com。
.PARAMETER CertificateThumbprint
  LocalMachine\My 中带私钥的正式证书指纹。
.PARAMETER ConfigPath
  目标服务器上已准备好的 config.local.toml；该文件必须位于发布包和 IIS 站点之外。
.PARAMETER MigrationMode
  Up：在停写窗口备份数据库与存储后运行 migration.exe up；Skip：明确不迁移。
.PARAMETER Plan
  仅完成只读检查并显示计划，不停止服务、不备份、不写文件。
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?$')]
    [string] $HostName,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Fa-f0-9 ]+$')]
    [string] $CertificateThumbprint,

    [Parameter(Mandatory = $true)]
    [string] $ConfigPath,

    [Parameter(Mandatory = $true)]
    [ValidateSet('Up', 'Skip')]
    [string] $MigrationMode,

    [string] $PackageRoot = $PSScriptRoot,
    [string] $SiteName = 'YfSystem',
    [string] $AppPoolName = 'YfSystem',
    [string] $ServiceName = 'YfSystemBackend',
    [string] $SiteRoot = 'C:\inetpub\yf_system\site',
    [string] $ServerRoot = 'C:\Services\yf_system',
    [string] $BackupRoot = 'C:\DeployBackups\yf_system',
    [ValidateRange(1, 65535)] [int] $HttpsPort = 443,
    [ValidateRange(1, 65535)] [int] $BackendPort = 8080,

    [string] $StorageRoot,
    [string] $DatabaseName,
    [string] $DatabaseHost = '127.0.0.1',
    [ValidateRange(1, 65535)] [int] $DatabasePort = 3306,
    [string] $DatabaseUser,
    [string] $MySqlDump = 'mysqldump.exe',
    [switch] $InitializeDatabase,

    [switch] $Plan
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Resolve-FullPath([string] $Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -notmatch '^[A-Za-z]:[\\/]') {
        throw '部署路径必须使用本地磁盘的绝对路径。'
    }
    return [System.IO.Path]::GetFullPath($Value).TrimEnd([char[]]'\/')
}

function Assert-NoReparsePoint([string] $PathValue) {
    $current = $PathValue
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "部署路径不能经过符号链接或目录联接：$PathValue"
            }
        }
        $current = Split-Path -Parent $current
    }
    if (Test-Path -LiteralPath $PathValue -PathType Container) {
        $links = @(Get-ChildItem -LiteralPath $PathValue -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
        if ($links.Count) { throw "部署目录中存在符号链接或目录联接：$PathValue" }
    }
}

function Assert-DisjointPaths([string[]] $Paths) {
    for ($i = 0; $i -lt $Paths.Count; $i++) {
        for ($j = $i + 1; $j -lt $Paths.Count; $j++) {
            if ((Test-IsWithin $Paths[$i] $Paths[$j]) -or (Test-IsWithin $Paths[$j] $Paths[$i])) {
                throw "部署、备份、发布包及存储目录不能重叠：$($Paths[$i]) / $($Paths[$j])"
            }
        }
    }
}

# The deployment template uses single-line TOML scalars; reject unsupported syntax.
function Read-DeployScalar([string] $Text, [string] $Section, [string] $Key) {
    $pattern = '(?ms)^\s*\[' + [regex]::Escape($Section) + '\][ \t]*(?:#[^\r\n]*)?\r?\n(?<body>.*?)(?=^\s*\[|\z)'
    $sections = [regex]::Matches($Text, $pattern)
    if ($sections.Count -ne 1) { throw "配置必须且只能包含一个 [$Section]。" }
    $values = [regex]::Matches($sections[0].Groups['body'].Value, '(?m)^[ \t]*' + [regex]::Escape($Key) + '[ \t]*=[ \t]*(?<value>[^\r\n]+)')
    if ($values.Count -ne 1) { throw "配置缺少或重复字段 $Section.$Key。" }
    $raw = $values[0].Groups['value'].Value.Trim()
    if ($raw -match '^"(?<text>[^"\\]*)"\s*(?:#.*)?$') { return $Matches['text'] }
    if ($raw -match "^'(?<text>[^']*)'\s*(?:#.*)?$") { return $Matches['text'] }
    if ($raw -match '^(?<text>true|false)\s*(?:#.*)?$') { return $Matches['text'] }
    throw "配置 $Section.$Key 请使用示例中的单行写法；Windows 路径请使用正斜线。"
}

function Read-SecretText([string] $Prompt) {
    $secure = Read-Host $Prompt -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer); $secure.Dispose() }
}

function Get-RelativePath([string] $BasePath, [string] $ChildPath) {
    $base = Resolve-FullPath $BasePath
    $child = Resolve-FullPath $ChildPath
    $baseUri = New-Object System.Uri(($base.TrimEnd([char[]]'\/') + [System.IO.Path]::DirectorySeparatorChar))
    $childUri = New-Object System.Uri($child)
    return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($childUri).ToString()).Replace('/', '\')
}

function Test-IsWithin([string] $ChildPath, [string] $ParentPath) {
    $child = Resolve-FullPath $ChildPath
    $parent = Resolve-FullPath $ParentPath
    $prefix = $parent + [System.IO.Path]::DirectorySeparatorChar
    return $child.Equals($parent, [System.StringComparison]::OrdinalIgnoreCase) -or
        $child.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-SafeDeployRoot([string] $PathValue, [string] $Name) {
    $full = Resolve-FullPath $PathValue
    $drive = [System.IO.Path]::GetPathRoot($full).TrimEnd([char[]]'\/')
    $windows = Resolve-FullPath $env:windir
    $programFiles = Resolve-FullPath $env:ProgramFiles
    if ($full -eq $drive -or $full -eq $windows -or $full -eq $programFiles -or
        (Test-IsWithin $full $windows) -or (Test-IsWithin $full $programFiles)) {
        throw "$Name 不能是磁盘根或系统目录：$full"
    }
    if ((Split-Path -Parent $full) -eq $drive) {
        throw "$Name 至少需要位于磁盘根下两级目录：$full"
    }
    return $full
}

function Assert-SimpleName([string] $Value, [string] $Name) {
    if ($Value -notmatch '^[A-Za-z0-9_.-]+$') { throw "$Name 仅允许字母、数字、点、下划线和短横线。" }
}

function Invoke-NativeChecked([string] $Command, [string[]] $Arguments, [string] $WorkingDirectory) {
    Push-Location $WorkingDirectory
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) { throw "命令失败（退出码 $LASTEXITCODE）：$Command" }
    }
    finally { Pop-Location }
}

function Test-PackageHashes([string] $Root) {
    $hashFile = Join-Path $Root 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $hashFile -PathType Leaf)) { throw "发布包缺少哈希清单：$hashFile" }
    $checked = 0
    $seen = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($hashFile)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $match = [regex]::Match($line, '^(?<hash>[A-Fa-f0-9]{64})  (?<path>.+)$')
        if (-not $match.Success) { throw "哈希清单格式错误：$line" }
        $relative = $match.Groups['path'].Value.Replace('/', '\')
        if ([System.IO.Path]::IsPathRooted($relative) -or $relative.Split('\') -contains '..') {
            throw "哈希清单包含越界路径：$relative"
        }
        $file = Resolve-FullPath (Join-Path $Root $relative)
        if ($seen.ContainsKey($file)) { throw "哈希清单重复文件：$relative" }
        $seen[$file] = $true
        if (-not (Test-IsWithin $file $Root) -or -not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "发布包文件缺失或越界：$relative"
        }
        $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        if (-not $actual.Equals($match.Groups['hash'].Value, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "发布包文件哈希不一致：$relative"
        }
        $checked++
    }
    if ($checked -lt 8) { throw "哈希清单异常，仅包含 $checked 个文件。" }
    foreach ($item in Get-ChildItem -LiteralPath $Root -File -Recurse -Force) {
        if ($item.FullName -ne $hashFile -and -not $seen.ContainsKey($item.FullName)) {
            throw "发布包包含未列入哈希清单的文件：$($item.Name)"
        }
    }
    return $checked
}

function Get-ServiceRecord([string] $Name) {
    $escaped = $Name.Replace("'", "''")
    return Get-CimInstance -ClassName Win32_Service -Filter "Name='$escaped'" -ErrorAction SilentlyContinue
}

function Stop-OwnedService([string] $Name) {
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $Name -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(45))
    }
}

function Copy-DirectoryContents([string] $Source, [string] $Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
    }
}

function Write-Utf8NoBom([string] $PathValue, [string] $Content) {
    [System.IO.File]::WriteAllText($PathValue, $Content.Trim() + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
}

function Assert-DirectoryCopy([string] $Source, [string] $Destination) {
    foreach ($file in Get-ChildItem -LiteralPath $Source -File -Force -Recurse) {
        $target = Join-Path $Destination (Get-RelativePath $Source $file.FullName)
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or
            (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
            throw '程序备份哈希校验失败，未进入替换阶段。'
        }
    }
}

function Set-PrivateDirectoryAcl([string] $PathValue, [switch] $ServiceRead) {
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $identity = New-Object System.Security.Principal.SecurityIdentifier($sid)
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    if ($ServiceRead) {
        $identity = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-19')
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $PathValue -AclObject $acl
}

function Grant-ServiceModify([string] $PathValue) {
    $acl = Get-Acl -LiteralPath $PathValue
    $identity = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-19')
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl -LiteralPath $PathValue -AclObject $acl
}

Assert-SimpleName $SiteName 'SiteName'
Assert-SimpleName $AppPoolName 'AppPoolName'
Assert-SimpleName $ServiceName 'ServiceName'

if (-not [Environment]::Is64BitOperatingSystem) { throw '仅支持 Windows x64 目标服务器。' }
if (-not [Environment]::Is64BitProcess) { throw '请使用 64 位 PowerShell 执行。' }
$PackageRoot = Resolve-FullPath $PackageRoot
$ConfigPath = Resolve-FullPath $ConfigPath
$SiteRoot = Assert-SafeDeployRoot $SiteRoot 'SiteRoot'
$ServerRoot = Assert-SafeDeployRoot $ServerRoot 'ServerRoot'
$BackupRoot = Assert-SafeDeployRoot $BackupRoot 'BackupRoot'

Assert-DisjointPaths @($SiteRoot, $ServerRoot, $BackupRoot, $PackageRoot)
foreach ($path in @($SiteRoot, $ServerRoot, $BackupRoot, $PackageRoot, $ConfigPath)) { Assert-NoReparsePoint $path }
if ((Test-IsWithin $ConfigPath $PackageRoot) -or (Test-IsWithin $ConfigPath $SiteRoot) -or
    (Test-IsWithin $ConfigPath $ServerRoot) -or (Test-IsWithin $ConfigPath $BackupRoot)) {
    throw 'ConfigPath 必须位于发布包、程序目录和备份目录之外。'
}

$requiredFiles = @(
    'site\index.html', 'site\web.config', 'server\server.exe', 'server\migration.exe',
    'tools\WinSW-x64.exe', 'deploy-manifest.json', 'SHA256SUMS.txt',
    'scripts\backup.ps1', 'scripts\backup-options.ps1'
)
foreach ($relative in $requiredFiles) {
    $path = Join-Path $PackageRoot $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "发布包缺少：$relative" }
}
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { throw "生产配置不存在：$ConfigPath" }
$hashCount = Test-PackageHashes $PackageRoot

$configText = [System.IO.File]::ReadAllText($ConfigPath)
if ((Read-DeployScalar $configText 'server' 'addr') -cne "127.0.0.1:$BackendPort") { throw "server.addr 必须为 127.0.0.1:$BackendPort。" }
if ((Read-DeployScalar $configText 'jwt' 'cookie_secure') -cne 'true') { throw 'cookie_secure 必须为 true。' }
if ((Read-DeployScalar $configText 'database' 'auto_migrate') -cne 'false') { throw 'auto_migrate 必须为 false。' }
if ((Read-DeployScalar $configText 'jwt' 'secret').Length -lt 32) { throw 'JWT secret 至少需要 32 字符。' }
$configuredDatabase = Read-DeployScalar $configText 'database' 'url'
$configuredStorage = Resolve-FullPath (Read-DeployScalar $configText 'storage' 'root')
if (-not $StorageRoot) { $StorageRoot = $configuredStorage }
$StorageRoot = Assert-SafeDeployRoot $StorageRoot 'StorageRoot'
if ($StorageRoot -ne $configuredStorage) { throw 'StorageRoot 与生产配置的 storage.root 不一致。' }
Assert-DisjointPaths @($SiteRoot, $ServerRoot, $BackupRoot, $PackageRoot, $StorageRoot)
Assert-NoReparsePoint $StorageRoot
if (-not (Test-Path -LiteralPath $StorageRoot -PathType Container)) { throw '请预先创建生产配置中的独立存储目录。' }
$publicOrigin = if ($HttpsPort -eq 443) { "https://$HostName" } else { "https://$($HostName):$HttpsPort" }
if ((Read-DeployScalar $configText 'web' 'base_url').TrimEnd('/') -ne $publicOrigin) { throw 'web.base_url 必须与部署的 HTTPS 地址一致。' }
$manifest = Get-Content -LiteralPath (Join-Path $PackageRoot 'deploy-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.backendProxy -ne "http://127.0.0.1:$BackendPort") { throw 'BackendPort 与发布包代理地址不一致，请使用相同端口重新构建。' }
foreach ($name in @('YF_CONFIG','YF_DATABASE_URL','YF_JWT_SECRET','YF_WEB_BASE_URL','YF_BOOTSTRAP_PASSWORD','YF_SMTP_HOST','YF_SMTP_PORT','YF_SMTP_USERNAME','YF_SMTP_PASSWORD','YF_SMTP_FROM')) {
    if ([Environment]::GetEnvironmentVariable($name, 'Machine')) { throw "请先清除会覆盖服务配置的机器级环境变量 $name。" }
}
if ($InitializeDatabase -and $MigrationMode -ne 'Up') { throw 'InitializeDatabase 必须和 MigrationMode Up 一起使用。' }

if ($MigrationMode -eq 'Up') {
    foreach ($item in @(
        @{ Name = 'StorageRoot'; Value = $StorageRoot },
        @{ Name = 'DatabaseName'; Value = $DatabaseName },
        @{ Name = 'DatabaseUser'; Value = $DatabaseUser }
    )) {
        if ([string]::IsNullOrWhiteSpace($item.Value)) { throw "MigrationMode=Up 时必须提供 $($item.Name)。" }
    }
    if ([string]::IsNullOrWhiteSpace($env:YF_BACKUP_DB_PASSWORD)) { throw 'MigrationMode=Up 时必须通过进程级 YF_BACKUP_DB_PASSWORD 提供备份账号密码。' }
    if ([string]::IsNullOrWhiteSpace($env:YF_DATABASE_URL)) { throw 'MigrationMode=Up 时必须通过进程级 YF_DATABASE_URL 提供迁移连接串。' }
    if ($env:YF_DATABASE_URL -cne $configuredDatabase) { throw '迁移连接串必须与生产配置 database.url 完全一致。' }
    try { $dbUri = [Uri]$configuredDatabase } catch { throw 'database.url 格式无效。' }
    if ($dbUri.Scheme -ne 'mysql' -or $dbUri.Host -ne $DatabaseHost -or
        $dbUri.AbsolutePath.TrimStart('/') -ne $DatabaseName -or
        ($dbUri.Port -gt 0 -and $dbUri.Port -ne $DatabasePort) -or
        ($dbUri.Port -lt 0 -and $DatabasePort -ne 3306)) { throw '备份数据库主机、端口和库名必须与生产配置一致。' }
    $StorageRoot = Resolve-FullPath $StorageRoot
    if (-not (Test-Path -LiteralPath $StorageRoot -PathType Container)) { throw "存储目录不存在：$StorageRoot" }
    if (-not (Get-Command $MySqlDump -ErrorAction SilentlyContinue)) { throw "未找到 mysqldump：$MySqlDump" }
}

$iisAssembly = Join-Path $env:windir 'System32\inetsrv\Microsoft.Web.Administration.dll'
$rewriteSchema = Join-Path $env:windir 'System32\inetsrv\config\schema\rewrite_schema.xml'
if (-not (Test-Path -LiteralPath $iisAssembly -PathType Leaf)) { throw '目标服务器未安装 IIS 管理服务组件。' }
if (-not (Test-Path -LiteralPath $rewriteSchema -PathType Leaf)) { throw '目标服务器未安装 IIS URL Rewrite 2。' }
if (-not (Get-Service -Name W3SVC -ErrorAction SilentlyContinue)) { throw '目标服务器未安装 IIS World Wide Web Publishing Service。' }
if (-not (Test-Path -LiteralPath (Join-Path $env:windir 'System32\vcruntime140.dll'))) { throw '请先安装 Microsoft Visual C++ x64 Runtime。' }
# --help loads the actual release executable without opening the database.
Invoke-NativeChecked (Join-Path $PackageRoot 'server\migration.exe') @('--help') $PackageRoot

Add-Type -Path $iisAssembly
$manager = New-Object Microsoft.Web.Administration.ServerManager
try {
    $appHostConfig = $manager.GetApplicationHostConfiguration()
    $enabledModules = @($appHostConfig.GetSection('system.webServer/globalModules').GetCollection() | ForEach-Object { $_.GetAttributeValue('name') })
    foreach ($requiredModule in @('StaticFileModule', 'DefaultDocumentModule', 'AnonymousAuthenticationModule', 'RewriteModule', 'ApplicationRequestRouting')) {
        if ($enabledModules -notcontains $requiredModule) { throw "IIS 缺少所需模块：$requiredModule" }
    }
    try { $proxySection = $appHostConfig.GetSection('system.webServer/proxy') }
    catch { throw '目标服务器未安装 Application Request Routing（ARR）。' }
    if ($null -eq $proxySection) { throw '目标服务器未安装 Application Request Routing（ARR）。' }

    $existingSite = $manager.Sites[$SiteName]
    if ($existingSite) {
        $existingRoot = Resolve-FullPath $existingSite.Applications['/'].VirtualDirectories['/'].PhysicalPath
        if (-not $existingRoot.Equals($SiteRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "同名 IIS 站点已指向其他目录：$existingRoot"
        }
    }
    if (-not $existingSite -and (Test-Path -LiteralPath $SiteRoot)) { throw '新站点的 SiteRoot 必须不存在，避免接管其他文件。' }
    foreach ($otherSite in $manager.Sites) {
        if ($otherSite.Name -eq $SiteName) { continue }
        foreach ($app in $otherSite.Applications) {
            if ($app.ApplicationPoolName -eq $AppPoolName) { throw '指定应用池已被其他站点使用，请使用独立名称。' }
        }
        foreach ($binding in $otherSite.Bindings) {
            if ($binding.Protocol -eq 'https' -and $binding.BindingInformation -match (':' + $HttpsPort + ':' + [regex]::Escape($HostName) + '$')) { throw 'HTTPS 域名和端口已被其他站点绑定。' }
        }
    }
}
finally { $manager.Dispose() }

$thumbprint = $CertificateThumbprint.Replace(' ', '').ToUpperInvariant()
$cert = Get-Item -LiteralPath "Cert:\LocalMachine\My\$thumbprint" -ErrorAction SilentlyContinue
if (-not $cert) { throw "LocalMachine\My 中找不到证书：$thumbprint" }
if (-not $cert.HasPrivateKey) { throw '指定证书没有私钥。' }
if ($cert.NotAfter -le (Get-Date) -or $cert.NotBefore -gt (Get-Date)) { throw '指定证书不在有效期内。' }
$dnsMatch = $false
foreach ($dns in $cert.DnsNameList.Unicode) {
    if ($dns -eq $HostName) { $dnsMatch = $true; break }
    if ($dns.StartsWith('*.') -and $HostName.EndsWith($dns.Substring(1), [System.StringComparison]::OrdinalIgnoreCase) -and $dns.Split('.').Count -eq $HostName.Split('.').Count) { $dnsMatch = $true; break }
}
if (-not $dnsMatch) { throw "证书名称不包含主机名：$HostName" }

$expectedWrapper = Join-Path $ServerRoot ($ServiceName + '.exe')
$existingService = Get-ServiceRecord $ServiceName
if ($existingService -and $existingService.PathName.Trim().Trim('"') -ne $expectedWrapper) {
    throw "同名 Windows 服务不属于此部署目录：$($existingService.PathName)"
}

$backendConnection = Get-NetTCPConnection -LocalPort $BackendPort -State Listen -ErrorAction SilentlyContinue
if (-not $existingService -and (Test-Path -LiteralPath $ServerRoot)) { throw '新服务的 ServerRoot 必须不存在，避免接管其他文件。' }
if ($existingService -and $existingService.StartName -notmatch '^(NT AUTHORITY\\)?LocalService$') { throw '现有服务未使用 LocalService；请先人工核对服务账号与目录授权。' }
if ($existingService -and (Test-Path -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName")) {
    $svcRegistry = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    if ($svcRegistry.PSObject.Properties['Environment']) { throw '现有服务包含独立环境覆盖，请先核对并清除。' }
}
if ($backendConnection -and -not $existingService) {
    throw "后端端口 $BackendPort 已被其他进程监听。"
}
if ($backendConnection -and $existingService) {
    $foreignListener = @($backendConnection | Where-Object {
        $listener = Get-CimInstance Win32_Process -Filter "ProcessId=$($_.OwningProcess)"
        -not $listener -or $listener.ParentProcessId -ne $existingService.ProcessId -or $listener.ExecutablePath -ne (Join-Path $ServerRoot 'server.exe')
    })
    if ($foreignListener.Count -gt 0) {
        throw "后端端口 $BackendPort 的监听进程不属于服务 $ServiceName。"
    }
}

$planData = [ordered]@{
    PackageRoot = $PackageRoot
    VerifiedFiles = $hashCount
    Site = "$SiteName -> $SiteRoot"
    Https = "https://${HostName}:$HttpsPort"
    BackendService = "$ServiceName -> $ServerRoot (127.0.0.1:$BackendPort)"
    BackupRoot = $BackupRoot
    MigrationMode = $MigrationMode
    ConfigSource = $ConfigPath
}
Write-Host '==> 目标服务器部署计划'
$planData | Format-List | Out-Host
if ($Plan) {
    Write-Host '==> Plan 完成：前置条件和发布包校验通过，未停止服务、未备份、未写入配置。'
    return
}

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$codeBackup = Join-Path $BackupRoot ("program_" + $stamp + '_' + [guid]::NewGuid().ToString('N'))
$oldSite = Join-Path $codeBackup 'site'
$oldServer = Join-Path $codeBackup 'server'
$hadSite = Test-Path -LiteralPath $SiteRoot -PathType Container
$hadServer = Test-Path -LiteralPath $ServerRoot -PathType Container
$migrationStarted = $false
$siteWasStarted = $false
$filesChanged = $false
$serviceInstalled = $false
$serviceWasRunning = $existingService -and $existingService.State -eq 'Running'
$bootstrapPassword = $null
if ($InitializeDatabase) {
    $bootstrapPassword = Read-SecretText '首次管理员密码（12-64 字符）'
    if ($bootstrapPassword.Length -lt 12 -or $bootstrapPassword.Length -gt 64 -or $bootstrapPassword -cne (Read-SecretText '再次输入首次管理员密码')) {
        throw '初始密码长度不合规或两次输入不一致，未开始部署。'
    }
}

try {
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $site = $manager.Sites[$SiteName]
        if ($site -and $site.State -eq [Microsoft.Web.Administration.ObjectState]::Started) {
            $siteWasStarted = $true
            $null = $site.Stop()
        }
    }
    finally { $manager.Dispose() }

    Stop-OwnedService $ServiceName
    if (Get-NetTCPConnection -LocalPort $BackendPort -State Listen -ErrorAction SilentlyContinue) { throw '服务停止后端口仍在监听，未替换程序。' }
    New-Item -ItemType Directory -Path $codeBackup -Force | Out-Null
    Set-PrivateDirectoryAcl $codeBackup
    $dataBackupRoot = Join-Path $codeBackup 'data'

    if ($MigrationMode -eq 'Up') {
        Write-Host '==> 在停写窗口备份数据库与存储'
        & (Join-Path $PackageRoot 'scripts\backup.ps1') -BackupRoot $dataBackupRoot -StorageRoot $StorageRoot `
            -DatabaseName $DatabaseName -DatabaseHost $DatabaseHost -DatabasePort $DatabasePort `
            -DatabaseUser $DatabaseUser -MySqlDump $MySqlDump
        # backup.ps1 throws on failure; robocopy success codes 1-7 are not failures.
    }

    New-Item -ItemType Directory -Path $codeBackup -Force | Out-Null
    if ($hadSite) {
        Write-Host "==> 备份原站点程序：$oldSite"
        Copy-DirectoryContents $SiteRoot $oldSite
        Assert-DirectoryCopy $SiteRoot $oldSite
    }
    if ($hadServer) {
        Write-Host "==> 备份原后端程序：$oldServer"
        Copy-DirectoryContents $ServerRoot $oldServer
        Assert-DirectoryCopy $ServerRoot $oldServer
    }

    $filesChanged = $true
    Assert-NoReparsePoint $SiteRoot
    Assert-NoReparsePoint $ServerRoot
    if (Test-Path -LiteralPath $SiteRoot) { Remove-Item -LiteralPath $SiteRoot -Recurse -Force }
    if (Test-Path -LiteralPath $ServerRoot) { Remove-Item -LiteralPath $ServerRoot -Recurse -Force }
    Copy-DirectoryContents (Join-Path $PackageRoot 'site') $SiteRoot
    Copy-DirectoryContents (Join-Path $PackageRoot 'server') $ServerRoot
    Set-PrivateDirectoryAcl $ServerRoot -ServiceRead
    $logDir = Join-Path $ServerRoot 'logs'
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    Grant-ServiceModify $logDir
    Grant-ServiceModify $StorageRoot
    Copy-Item -LiteralPath $ConfigPath -Destination (Join-Path $ServerRoot 'config.local.toml') -Force
    Copy-Item -LiteralPath (Join-Path $PackageRoot 'tools\WinSW-x64.exe') -Destination $expectedWrapper -Force

    $serviceXml = @"
<service>
  <id>$ServiceName</id>
  <name>$ServiceName</name>
  <description>yf_system Rust backend</description>
  <executable>%BASE%\server.exe</executable>
  <workingdirectory>%BASE%</workingdirectory>
  <env name="YF_CONFIG" value="%BASE%\config.local.toml"/>
  <serviceaccount><domain>NT AUTHORITY</domain><user>LocalService</user></serviceaccount>
  <logpath>%BASE%\logs</logpath>
  <startmode>Automatic</startmode>
  <onfailure action="restart" delay="10 sec" />
  <resetfailure>1 hour</resetfailure>
  <stoptimeout>30 sec</stoptimeout>
  <log mode="roll-by-size-time">
    <sizeThreshold>10240</sizeThreshold>
    <pattern>yyyyMMdd</pattern>
    <autoRollAtTime>00:00:00</autoRollAtTime>
    <zipOlderThanNumDays>14</zipOlderThanNumDays>
    <zipDateFormat>yyyyMM</zipDateFormat>
  </log>
</service>
"@
    $serviceXmlPath = Join-Path $ServerRoot ($ServiceName + '.xml')
    Write-Utf8NoBom $serviceXmlPath $serviceXml

    if (-not $existingService) {
        Write-Host '==> 安装 Rust 后端 Windows 服务'
        Invoke-NativeChecked $expectedWrapper @('install') $ServerRoot
        $serviceInstalled = $true
    }

    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $pool = $manager.ApplicationPools[$AppPoolName]
        if (-not $pool) { $pool = $manager.ApplicationPools.Add($AppPoolName) }
        $pool.ManagedRuntimeVersion = ''
        $pool.ManagedPipelineMode = [Microsoft.Web.Administration.ManagedPipelineMode]::Integrated
        $pool.ProcessModel.IdentityType = [Microsoft.Web.Administration.ProcessModelIdentityType]::ApplicationPoolIdentity

        $site = $manager.Sites[$SiteName]
        if (-not $site) {
            $site = $manager.Sites.Add($SiteName, 'https', "*:${HttpsPort}:$HostName", $SiteRoot)
        }
        else {
            $site.Applications['/'].VirtualDirectories['/'].PhysicalPath = $SiteRoot
        }
        $site.Applications['/'].ApplicationPoolName = $AppPoolName
        $site.ServerAutoStart = $false

        $bindingInfo = "*:${HttpsPort}:$HostName"
        $binding = $site.Bindings | Where-Object { $_.Protocol -eq 'https' -and $_.BindingInformation -eq $bindingInfo } | Select-Object -First 1
        if (-not $binding) { $binding = $site.Bindings.Add($bindingInfo, $cert.GetCertHash(), 'My') }
        $binding.CertificateHash = $cert.GetCertHash()
        $binding.CertificateStoreName = 'My'
        $binding.SetAttributeValue('sslFlags', 1)

        $appHostConfig = $manager.GetApplicationHostConfiguration()
        $proxy = $appHostConfig.GetSection('system.webServer/proxy')
        $proxy.SetAttributeValue('enabled', $true)
        $proxy.SetAttributeValue('preserveHostHeader', $true)
        $allowed = $appHostConfig.GetSection('system.webServer/rewrite/allowedServerVariables').GetCollection()
        $hasForwardedFor = $false
        foreach ($entry in $allowed) {
            if ($entry.GetAttributeValue('name') -eq 'HTTP_X_FORWARDED_FOR') { $hasForwardedFor = $true; break }
        }
        if (-not $hasForwardedFor) {
            $entry = $allowed.CreateElement('add')
            $entry.SetAttributeValue('name', 'HTTP_X_FORWARDED_FOR')
            $allowed.Add($entry)
        }
        $manager.CommitChanges()
        $siteAcl = Get-Acl -LiteralPath $SiteRoot
        $poolIdentity = New-Object System.Security.Principal.NTAccount("IIS AppPool\$AppPoolName")
        $siteAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($poolIdentity, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
        Set-Acl -LiteralPath $SiteRoot -AclObject $siteAcl
        $anonymous = $manager.GetApplicationHostConfiguration().GetSection('system.webServer/security/authentication/anonymousAuthentication', $SiteName)
        $anonymous.SetAttributeValue('enabled', $true)
        $anonymous.SetAttributeValue('userName', '')
        $manager.CommitChanges()
    }
    finally { $manager.Dispose() }

    if ($MigrationMode -eq 'Up') {
        Write-Host '==> 执行数据库迁移（从此不自动回退旧程序）'
        $migrationStarted = $true
        $previousDatabaseUrl = $env:DATABASE_URL
        $previousBootstrap = $env:YF_BOOTSTRAP_PASSWORD
        try {
            $env:DATABASE_URL = $env:YF_DATABASE_URL
            $env:YF_BOOTSTRAP_PASSWORD = $bootstrapPassword
            Invoke-NativeChecked (Join-Path $ServerRoot 'migration.exe') @('up') $ServerRoot
        }
        finally { $env:DATABASE_URL = $previousDatabaseUrl; $env:YF_BOOTSTRAP_PASSWORD = $previousBootstrap; $bootstrapPassword = $null }
    }

    Write-Host '==> 启动后端服务和 IIS 站点'
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(45))
    $healthy = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$BackendPort/health" -TimeoutSec 2
            if ($health.status -eq 'ok' -and $health.db -eq 'up') { $healthy = $true; break }
        } catch { }
        Start-Sleep -Seconds 2
    }
    if (-not $healthy) { throw '后端在等待期内未通过健康检查，请查看后端 logs。' }
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $manager.Sites[$SiteName].ServerAutoStart = $true
        $manager.CommitChanges()
        $null = $manager.Sites[$SiteName].Start()
    }
    finally { $manager.Dispose() }

    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$BackendPort/health" -TimeoutSec 20
    if ($health.status -ne 'ok') { throw '后端 /health 未返回 status=ok。' }
    $rootResponse = Invoke-WebRequest -UseBasicParsing -Uri "https://${HostName}:$HttpsPort/" -TimeoutSec 20
    if ($rootResponse.StatusCode -ne 200) { throw 'IIS 首页检查失败。' }
    $expectedHtml = [IO.File]::ReadAllText((Join-Path $SiteRoot 'index.html')).Trim()
    if ([string]$rootResponse.Content.Trim() -cne $expectedHtml) { throw 'HTTPS 首页内容与本次发布不一致，请核对 DNS、缓存和 IIS 绑定。' }
    $deepResponse = Invoke-WebRequest -UseBasicParsing -Uri "https://${HostName}:$HttpsPort/login" -TimeoutSec 20
    if ($deepResponse.StatusCode -ne 200) { throw 'IIS 深链接检查失败。' }
    if ([string]$deepResponse.Content.Trim() -cne $expectedHtml) { throw 'IIS 深链接未返回本次 SPA 页面。' }
    $captcha = Invoke-RestMethod -Uri "https://${HostName}:$HttpsPort/api/v1/auth/captcha" -TimeoutSec 20
    if (-not $captcha.captchaId -or -not $captcha.svg) { throw '经 IIS 的验证码 API 契约检查失败。' }

    Write-Host "==> 正式部署完成：https://${HostName}:$HttpsPort/"
    Write-Host "==> 程序备份：$codeBackup"
}
catch {
    $failure = $_
    if (-not $filesChanged) {
        # Backup or stop failure must never delete original files.
        if ($serviceWasRunning) { Start-Service -Name $ServiceName -ErrorAction SilentlyContinue }
        if ($siteWasStarted) {
            $rollbackManager = New-Object Microsoft.Web.Administration.ServerManager
            try { if ($rollbackManager.Sites[$SiteName]) { $null = $rollbackManager.Sites[$SiteName].Start() } }
            finally { $rollbackManager.Dispose() }
        }
    }
    else {
        if ($existingService -or $serviceInstalled) { Stop-OwnedService $ServiceName }
        $failureManager = New-Object Microsoft.Web.Administration.ServerManager
        try { if ($failureManager.Sites[$SiteName]) { $null = $failureManager.Sites[$SiteName].Stop() } }
        finally { $failureManager.Dispose() }
        Write-Warning "已停止本次站点与服务，保留新程序、日志和原程序备份：$codeBackup。请修复后重试；不自动回退 IIS 或数据库。"
        if ($migrationStarted) { Write-Warning '数据库迁移已经启动，禁止只回退旧程序。' }
    }
    throw $failure
}
