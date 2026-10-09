#Requires -Version 5.1
<#
.SYNOPSIS
Rebuilds and restarts the local development backend (Release build) and the Vite dev server.

.DESCRIPTION
Order: back up the database -> isolated Release build -> stop the old backend -> --migrate-database ->
start the backend and wait for /health -> restart `npm run dev` and wait for the home page.
Only a local database (127.0.0.1/localhost) is accepted. Only listeners that are clearly this
project's backend (Yf.Api.dll) or Vite (web\node_modules\...vite) are stopped; any other owner aborts.
Logs, the backup and result.json go to .artifacts/runtime/restart-dev/<UTC timestamp>/.
After a successful backup only the newest -KeepBackups (default 5) before-restart.sql files are kept;
older ones are deleted from their run directories (logs and result.json stay).

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
    # Keep only the newest N before-restart.sql backups (each lives in its timestamped run directory).
    [ValidateRange(1, 100)][int]$KeepBackups = 5,
    [switch]$SkipFrontend,
    [int]$BackendPort = 8080,
    [int]$FrontendPort = 5180
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$apiRoot = Join-Path $projectRoot 'server_dotnet\Yf.Api'
$webRoot = Join-Path $projectRoot 'web'
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

function Start-DetachedLogged([string]$WorkingDirectory, [string]$CommandLine, [string]$StdoutLog, [string]$StderrLog) {
    # Start-Process with output redirection creates the child with handle inheritance, so a long-running
    # server would also inherit this script's own stdout/stderr and keep any caller that captures them
    # (a pipe, CI, an agent) waiting until the server exits. Shell-execute cmd.exe instead (no inherited
    # handles) and let cmd write the logs. The returned process is cmd.exe; it exits when the server does.
    $arguments = '/d /c "' + $CommandLine + ' 1>"' + $StdoutLog + '" 2>"' + $StderrLog + '""'
    return Start-Process cmd.exe -ArgumentList $arguments -WorkingDirectory $WorkingDirectory -WindowStyle Hidden -PassThru
}

function Remove-OldRestartBackups([string]$RunsRoot, [int]$Keep) {
    # Only exact before-restart.sql files inside timestamped run directories are removed.
    if (!(Test-Path -LiteralPath $RunsRoot -PathType Container)) { return }
    $backups = @(Get-ChildItem -LiteralPath $RunsRoot -Directory |
        Where-Object { $_.Name -match '^\d{8}-\d{6}-\d{3}$' -and !($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } |
        ForEach-Object { Get-Item -LiteralPath (Join-Path $_.FullName 'before-restart.sql') -ErrorAction SilentlyContinue } |
        Where-Object { $_ -and !($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } |
        Sort-Object { $_.Directory.Name } -Descending)
    foreach ($old in @($backups | Select-Object -Skip $Keep)) {
        Remove-Item -LiteralPath $old.FullName -Force
        Write-Host "Pruned old backup: $($old.FullName)"
    }
}

function Get-ConnectionOption($Builder, [string[]]$Names, [switch]$Required) {
    foreach ($name in $Names) {
        if ($Builder.ContainsKey($name)) { return [string]$Builder[$name] }
    }
    if ($Required) { throw ('Connection string is missing ' + ($Names -join '/')) }
    return ''
}

function ConvertTo-MySqlOption([string]$Value) {
    return '"' + $Value.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n') + '"'
}

function Invoke-NativeLogged([string]$FilePath, [string[]]$Arguments, [string]$LogPath) {
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $FilePath @Arguments *> $LogPath
        return [int]$LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
}

# --- Resolve configuration (never print the connection string) ---
if ([string]::IsNullOrWhiteSpace($ConfigPath)) { $ConfigPath = Join-Path $apiRoot 'appsettings.Local.json' }
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
if (!(Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { throw "Config file not found: $ConfigPath" }
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$builder = [System.Data.Common.DbConnectionStringBuilder]::new()
$builder.set_ConnectionString($config.App.ConnectionString)
$databaseServer = Get-ConnectionOption $builder @('server', 'host', 'data source') -Required
if ($databaseServer -notin @('127.0.0.1', 'localhost', '::1')) { throw 'Only a local database is allowed.' }
if ($DatabaseName) { $builder['database'] = $DatabaseName }
$DatabaseName = Get-ConnectionOption $builder @('database', 'initial catalog') -Required
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
$stagedApiDirectory = Join-Path $runRoot 'api'
$apiDll = Join-Path $stagedApiDirectory 'Yf.Api.dll'

# --- Back up the database while the old backend is still serving ---
$backupPath = $null
if (!$SkipBackup) {
    if (!$MysqlDumpPath) {
        $cmd = Get-Command mysqldump.exe -ErrorAction SilentlyContinue
        if ($cmd) { $MysqlDumpPath = $cmd.Source }
    }
    if (!$MysqlDumpPath) { throw 'mysqldump.exe not found; pass -MysqlDumpPath or -SkipBackup.' }
    $backupPath = Join-Path $runRoot 'before-restart.sql'
    $port = Get-ConnectionOption $builder @('port')
    if ([string]::IsNullOrWhiteSpace($port)) { $port = '3306' }
    $databaseUser = Get-ConnectionOption $builder @('user id', 'user', 'uid') -Required
    $databasePassword = Get-ConnectionOption $builder @('password', 'pwd')
    $defaultsPath = Join-Path $runRoot ('.mysql-' + [Guid]::NewGuid().ToString('N') + '.cnf')
    try {
        $defaultsLines = @(
            '[client]',
            ('host=' + (ConvertTo-MySqlOption $databaseServer)),
            ('port=' + $port),
            ('user=' + (ConvertTo-MySqlOption $databaseUser)),
            ('password=' + (ConvertTo-MySqlOption $databasePassword)),
            'protocol=TCP',
            'default-character-set=utf8mb4'
        )
        [IO.File]::WriteAllLines($defaultsPath, $defaultsLines, (New-Object Text.UTF8Encoding($false)))
        $dumpExit = Invoke-NativeLogged $MysqlDumpPath @(
            "--defaults-file=$defaultsPath", '--single-transaction',
            '--default-character-set=utf8mb4', "--result-file=$backupPath", $DatabaseName
        ) (Join-Path $runRoot 'database-backup.log')
        if ($dumpExit -ne 0 -or !(Test-Path -LiteralPath $backupPath -PathType Leaf) -or
            (Get-Item -LiteralPath $backupPath).Length -eq 0) {
            throw 'Database backup failed; nothing was stopped.'
        }
    } finally {
        if (Test-Path -LiteralPath $defaultsPath -PathType Leaf) { Remove-Item -LiteralPath $defaultsPath -Force }
    }
    Write-Host "Backup: $backupPath"
    Remove-OldRestartBackups (Split-Path -Parent $runRoot) $KeepBackups
}

# --- Backend: build in isolation, stop, migrate, start ---
$buildExit = Invoke-NativeLogged (Get-Command dotnet).Source @(
    'build', (Join-Path $apiRoot 'Yf.Api.csproj'), '--configuration', 'Release', '--output', $stagedApiDirectory
) (Join-Path $runRoot 'build.log')
if ($buildExit -ne 0 -or !(Test-Path -LiteralPath $apiDll -PathType Leaf)) {
    throw "Build failed; the running backend was not stopped. See $runRoot\build.log"
}
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
    $migrationExit = Invoke-NativeLogged (Get-Command dotnet).Source @($apiDll, '--migrate-database') (Join-Path $runRoot 'migration.log')
    if ($migrationExit -ne 0) { throw "Migration failed; see $runRoot\migration.log" }
    $backend = Start-DetachedLogged $apiRoot ('"' + (Get-Command dotnet).Source + '" "' + $apiDll + '"') `
        (Join-Path $runRoot 'backend.stdout.log') (Join-Path $runRoot 'backend.stderr.log')
} finally { Pop-Location }

$backendReady = Wait-Until { $h = Invoke-RestMethod $healthUrl -TimeoutSec 2; $h.status -eq 'ok' -and $h.db -eq 'up' } $backend 45
if (!$backendReady) { throw "Backend not healthy; see $runRoot\backend.*.log" }
$backendPid = (Get-ListenerOwner $BackendPort).ProcessId
Write-Host "Backend ready: pid $backendPid, $healthUrl"

# --- Frontend: restart Vite dev server (predev regenerates preview bundles) ---
$frontendPid = $null
if (!$SkipFrontend) {
    if ($frontendOwner) { Write-Host "Stopping Vite pid $($frontendOwner.ProcessId)"; Stop-Owner $frontendOwner }
    $frontend = Start-DetachedLogged $webRoot "npm run dev -- --host 127.0.0.1 --port $FrontendPort --strictPort" `
        (Join-Path $runRoot 'frontend.stdout.log') (Join-Path $runRoot 'frontend.stderr.log')
    $frontendReady = Wait-Until { (Invoke-WebRequest $frontendUrl -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } $frontend 90
    if (!$frontendReady) { throw "Frontend not ready; see $runRoot\frontend.*.log" }
    $frontendPid = (Get-ListenerOwner $FrontendPort).ProcessId
    Write-Host "Frontend ready: pid $frontendPid, $frontendUrl"
}

$result = [ordered]@{
    backendPid = $backendPid; backendUrl = "http://127.0.0.1:$BackendPort"
    frontendPid = $frontendPid; frontendUrl = if ($SkipFrontend) { $null } else { $frontendUrl }
    database = $DatabaseName; storageRoot = $StorageRoot; backup = $backupPath
}
$result | ConvertTo-Json | Set-Content (Join-Path $runRoot 'result.json') -Encoding utf8
$result | ConvertTo-Json
