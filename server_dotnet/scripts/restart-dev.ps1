#Requires -Version 5.1
<#
.SYNOPSIS
Rebuilds and restarts the local development backend (Release build) and the Vite dev server.

.DESCRIPTION
Order: back up the database -> stop the old backend -> Release build -> --migrate-database ->
start the backend and wait for /health -> restart `npm run dev` and wait for the home page.
Only a local database (127.0.0.1/localhost) is accepted. Only listeners that are clearly this
project's backend (Yf.Api.dll) or Vite (web\node_modules\...vite) are stopped; any other owner aborts.
Logs, the backup and result.json go to .artifacts/runtime/restart-dev/<UTC timestamp>/.

.EXAMPLE
powershell -NoProfile -File .\scripts\restart-dev.ps1 `
    -DatabaseName yf_system_dev_20260923_011050 `
    -StorageRoot ..\.artifacts\runtime-data\dev-20260923-011050
#>
[CmdletBinding()]
param(
    # Defaults to Yf.Api/appsettings.Local.json (git-ignored).
    [string]$ConfigPath,
    # Overrides the database in the config's connection string.
    [string]$DatabaseName,
    # Overrides App.StorageRoot from the config.
    [string]$StorageRoot,
    [string]$MysqlDumpPath,
    [switch]$SkipBackup,
    [switch]$SkipFrontend,
    [int]$BackendPort = 8080,
    [int]$FrontendPort = 5180
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$apiRoot = Join-Path $projectRoot 'server_dotnet\Yf.Api'
$webRoot = Join-Path $projectRoot 'web'
$apiDll = Join-Path $apiRoot 'bin\Release\net8.0\Yf.Api.dll'
$healthUrl = "http://127.0.0.1:$BackendPort/health"
$frontendUrl = "http://127.0.0.1:$FrontendPort/"

function Get-ListenerOwner([int]$Port) {
    $owners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess | Sort-Object -Unique)
    if ($owners.Count -eq 0) { return $null }
    if ($owners.Count -gt 1) { throw "Port $Port has more than one listener; stop them manually." }
    return Get-CimInstance Win32_Process -Filter "ProcessId=$($owners[0])"
}

function Stop-Owner($Process) {
    Stop-Process -Id $Process.ProcessId -Force
    Wait-Process -Id $Process.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
}

function Wait-Until([scriptblock]$Probe, $Process, [int]$Seconds) {
    for ($i = 0; $i -lt $Seconds * 2; $i++) {
        if ($Process) { $Process.Refresh(); if ($Process.HasExited) { return $false } }
        try { if (& $Probe) { return $true } } catch { }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

# --- Resolve configuration (never print the connection string) ---
if ([string]::IsNullOrWhiteSpace($ConfigPath)) { $ConfigPath = Join-Path $apiRoot 'appsettings.Local.json' }
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
if (!(Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { throw "Config file not found: $ConfigPath" }
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$builder = [System.Data.Common.DbConnectionStringBuilder]::new()
$builder.set_ConnectionString($config.App.ConnectionString)
if ($builder['server'] -notin @('127.0.0.1', 'localhost', '::1')) { throw 'Only a local database is allowed.' }
if ($DatabaseName) { $builder['database'] = $DatabaseName }
$DatabaseName = $builder['database']
if (!$StorageRoot) { $StorageRoot = $config.App.StorageRoot }
if (!$StorageRoot) { throw 'StorageRoot is not configured; pass -StorageRoot.' }
$StorageRoot = [IO.Path]::GetFullPath($StorageRoot)
if (!(Test-Path -LiteralPath $StorageRoot -PathType Container)) { throw "Storage root not found: $StorageRoot" }

# --- Check current listeners before touching anything ---
$backendOwner = Get-ListenerOwner $BackendPort
if ($backendOwner -and ($backendOwner.Name -ne 'dotnet.exe' -or $backendOwner.CommandLine -notmatch 'Yf\.Api\.dll')) {
    throw "Port $BackendPort is owned by an unexpected process (pid $($backendOwner.ProcessId))."
}
$frontendOwner = $null
if (!$SkipFrontend) {
    $frontendOwner = Get-ListenerOwner $FrontendPort
    if ($frontendOwner -and ($frontendOwner.Name -ne 'node.exe' -or
            !$frontendOwner.CommandLine.Contains((Join-Path $webRoot 'node_modules')) -or
            $frontendOwner.CommandLine -notmatch 'vite')) {
        throw "Port $FrontendPort is owned by an unexpected process (pid $($frontendOwner.ProcessId))."
    }
}

$runRoot = Join-Path $projectRoot ('.artifacts\runtime\restart-dev\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
Write-Host "Run directory: $runRoot"

# --- Back up the database while the old backend is still serving ---
$backupPath = $null
if (!$SkipBackup) {
    if (!$MysqlDumpPath) {
        $cmd = Get-Command mysqldump.exe -ErrorAction SilentlyContinue
        if ($cmd) { $MysqlDumpPath = $cmd.Source }
        else {
            $MysqlDumpPath = @('C:\Program Files\MySQL', 'D:\Program Files\MySQL') |
                Where-Object { Test-Path $_ } |
                ForEach-Object { Get-ChildItem $_ -Recurse -Filter mysqldump.exe -ErrorAction SilentlyContinue } |
                Select-Object -First 1 -ExpandProperty FullName
        }
    }
    if (!$MysqlDumpPath) { throw 'mysqldump.exe not found; pass -MysqlDumpPath or -SkipBackup.' }
    $backupPath = Join-Path $runRoot 'before-restart.sql'
    $port = if ($builder.ContainsKey('port')) { $builder['port'] } else { '3306' }
    $priorMysqlPassword = $env:MYSQL_PWD
    try {
        $env:MYSQL_PWD = $builder['password']
        & $MysqlDumpPath --host=127.0.0.1 "--port=$port" "--user=$($builder['user id'])" --single-transaction `
            --default-character-set=utf8mb4 "--result-file=$backupPath" $DatabaseName
        if ($LASTEXITCODE -ne 0 -or (Get-Item $backupPath).Length -eq 0) { throw 'Database backup failed; nothing was stopped.' }
    } finally { $env:MYSQL_PWD = $priorMysqlPassword }
    Write-Host "Backup: $backupPath"
}

# --- Backend: stop, build, migrate, start ---
# Stop first: the running process locks bin\Release, so building beforehand would fail.
if ($backendOwner) { Write-Host "Stopping backend pid $($backendOwner.ProcessId)"; Stop-Owner $backendOwner }

$env:App__ConnectionString = $builder.ConnectionString
$env:App__StorageRoot = $StorageRoot
$env:App__AutoInitializeDatabase = 'false'
$env:App__WebBaseUrl = "http://127.0.0.1:$FrontendPort"
$env:App__CookieSecure = 'false'
$env:App__WorkerEnabled = 'true'
$env:App__CopyWorkerEnabled = 'true'
$env:ASPNETCORE_URLS = "http://127.0.0.1:$BackendPort"
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:YF_CONFIG_PATH = $ConfigPath
Remove-Item Env:YF_BOOTSTRAP_PASSWORD -ErrorAction SilentlyContinue

Push-Location $apiRoot
try {
    & dotnet build .\Yf.Api.csproj -c Release *> (Join-Path $runRoot 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Build failed; see $runRoot\build.log" }
    & dotnet $apiDll --migrate-database *> (Join-Path $runRoot 'migration.log')
    if ($LASTEXITCODE -ne 0) { throw "Migration failed; see $runRoot\migration.log" }
    $backend = Start-Process (Get-Command dotnet).Source -ArgumentList ('"' + $apiDll + '"') -WorkingDirectory $apiRoot `
        -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $runRoot 'backend.stdout.log') `
        -RedirectStandardError (Join-Path $runRoot 'backend.stderr.log')
} finally { Pop-Location }

$backendReady = Wait-Until { $h = Invoke-RestMethod $healthUrl -TimeoutSec 2; $h.status -eq 'ok' -and $h.db -eq 'up' } $backend 45
if (!$backendReady) { throw "Backend not healthy; see $runRoot\backend.*.log" }
Write-Host "Backend ready: pid $($backend.Id), $healthUrl"

# --- Frontend: restart Vite dev server (predev regenerates preview bundles) ---
$frontendPid = $null
if (!$SkipFrontend) {
    if ($frontendOwner) { Write-Host "Stopping Vite pid $($frontendOwner.ProcessId)"; Stop-Owner $frontendOwner }
    $frontend = Start-Process cmd.exe -WorkingDirectory $webRoot -WindowStyle Hidden -PassThru `
        -ArgumentList '/c', "npm run dev -- --host 127.0.0.1 --port $FrontendPort --strictPort" `
        -RedirectStandardOutput (Join-Path $runRoot 'frontend.stdout.log') `
        -RedirectStandardError (Join-Path $runRoot 'frontend.stderr.log')
    $frontendReady = Wait-Until { (Invoke-WebRequest $frontendUrl -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } $frontend 90
    if (!$frontendReady) { throw "Frontend not ready; see $runRoot\frontend.*.log" }
    $frontendPid = (Get-ListenerOwner $FrontendPort).ProcessId
    Write-Host "Frontend ready: pid $frontendPid, $frontendUrl"
}

$result = [ordered]@{
    backendPid = $backend.Id; backendUrl = "http://127.0.0.1:$BackendPort"
    frontendPid = $frontendPid; frontendUrl = if ($SkipFrontend) { $null } else { $frontendUrl }
    database = $DatabaseName; storageRoot = $StorageRoot; backup = $backupPath
}
$result | ConvertTo-Json | Set-Content (Join-Path $runRoot 'result.json') -Encoding utf8
$result | ConvertTo-Json
