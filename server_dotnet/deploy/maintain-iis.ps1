#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
Back up, upgrade or restore an existing dedicated Yf ASP.NET Core IIS site.
.DESCRIPTION
Runs on the target server. The named site's dedicated pool is stopped for the
entire operation. Failures leave that pool stopped and preserve all old data.
Restore writes only to a new empty database, storage directory and application
directory; it never drops or overwrites the old database.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Backup','Upgrade','Restore')][string]$Action,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9_-]+$')][string]$SiteName,
    [Parameter(Mandatory=$true)][string]$BackupDirectory,
    [string]$PackageRoot,
    [string]$NewSiteRoot,
    [string]$RestoreConfigPath,
    [string]$MySqlDump = 'mysqldump.exe',
    [string]$MySql = 'mysql.exe',
    [switch]$MigrateDatabase
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'maintenance-common.ps1')

function Test-YfWorkersStopped([string]$Pool) {
    $manager=New-Object Microsoft.Web.Administration.ServerManager
    try { return @($manager.WorkerProcesses | Where-Object { $_.AppPoolName -eq $Pool }).Count -eq 0 }
    finally { $manager.Dispose() }
}
function Set-YfSiteConfig([string]$Root,[string]$ExternalConfig) {
    Set-YfExternalConfigurationFallback $Root
    $file = Join-Path $Root 'web.config'
    [xml]$xml = Get-Content -LiteralPath $file -Raw -Encoding UTF8
    $asp = $xml.SelectSingleNode('//aspNetCore')
    if (!$asp) { throw 'ASP.NET Core IIS configuration missing.' }
    $variables = $asp.SelectSingleNode('environmentVariables')
    if (!$variables) { $variables=$xml.CreateElement('environmentVariables'); $asp.AppendChild($variables) | Out-Null }
    foreach ($pair in @(@('YF_CONFIG_PATH',$ExternalConfig),@('ASPNETCORE_ENVIRONMENT','Production'))) {
        foreach ($existing in @($variables.SelectNodes('environmentVariable'))) {
            if ($existing.GetAttribute('name') -eq $pair[0]) { $variables.RemoveChild($existing) | Out-Null }
        }
        $node=$xml.CreateElement('environmentVariable'); $node.SetAttribute('name',$pair[0]); $node.SetAttribute('value',$pair[1]); $variables.AppendChild($node) | Out-Null
    }
    $xml.Save($file)
}
function Grant-YfApplicationAccess([string]$Root,$Config,[string]$Pool) {
    $identity='IIS AppPool\'+$Pool
    foreach ($grant in @(@($Root,"${identity}:(OI)(CI)RX"),@($Config.Storage,"${identity}:(OI)(CI)M"))) {
        & icacls.exe $grant[0] /grant $grant[1] | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Unable to grant application pool access.' }
    }
    Protect-YfConfigurationFile $Config.Path $identity
}
function Invoke-YfMigration([string]$Root,[string]$ExternalConfig) {
    $saved=@{}
    foreach ($item in Get-ChildItem Env:) {
        if ($item.Name -match '^App(__|:)' -or $item.Name -eq 'YF_CONFIG_PATH') { $saved[$item.Name]=$item.Value; Remove-Item -LiteralPath ('Env:\'+$item.Name) }
    }
    try {
        $env:YF_CONFIG_PATH=$ExternalConfig
        Push-Location $Root
        try {
            & dotnet (Join-Path $Root 'Yf.Api.dll') --migrate-database; if ($LASTEXITCODE -ne 0) { throw 'Explicit database migration failed; pool remains stopped.' }
            # Legacy file rows are converted to shared content here, not during IIS startup (startupTimeLimit).
            & dotnet (Join-Path $Root 'Yf.Api.dll') --convert-file-blobs; if ($LASTEXITCODE -ne 0) { throw 'File content conversion failed; pool remains stopped.' }
        }
        finally { Pop-Location }
    } finally {
        Remove-Item Env:\YF_CONFIG_PATH -ErrorAction SilentlyContinue
        foreach ($name in $saved.Keys) { Set-Item -LiteralPath ('Env:\'+$name) -Value $saved[$name] }
    }
}

Import-Module WebAdministration -ErrorAction Stop
$site=Get-Website -Name $SiteName -ErrorAction Stop
if (!$site -or $site.Name -ne $SiteName) { throw 'Named IIS site was not found.' }
$pool=[string]$site.applicationPool
if ($pool -notmatch '^[A-Za-z0-9_-]+$') { throw 'Unexpected application pool name.' }
if (@(Get-Website | Where-Object { $_.applicationPool -eq $pool -and $_.Name -ne $SiteName }).Count -or @(Get-WebApplication | Where-Object { $_.applicationPool -eq $pool }).Count) { throw 'Maintenance requires a dedicated application pool with no child applications.' }
$currentRoot=Get-YfFullPath ([Environment]::ExpandEnvironmentVariables($site.physicalPath))
Assert-YfNoLinks $currentRoot
if (!(Test-Path -LiteralPath (Join-Path $currentRoot 'Yf.Api.dll'))) { throw 'The named site is not a Yf ASP.NET Core deployment.' }
[xml]$currentXml=Get-Content -LiteralPath (Join-Path $currentRoot 'web.config') -Raw -Encoding UTF8
$configNodes=@($currentXml.SelectNodes('//aspNetCore/environmentVariables/environmentVariable[@name="YF_CONFIG_PATH"]'))
if ($configNodes.Count -ne 1) { throw 'Exactly one external YF_CONFIG_PATH is required in the current IIS deployment.' }
$currentConfig=Read-YfMaintenanceConfig $configNodes[0].GetAttribute('value')
Assert-YfPublishedConfig $currentRoot -AllowConfigPath
Assert-YfEffectiveConfiguration $SiteName $pool $currentConfig.Path $currentConfig.Origin
if ($currentConfig.Origin -notlike 'https://*' -or $currentConfig.Config.App.CookieSecure -ne $true) { throw 'Maintenance requires a production HTTPS origin and secure cookies.' }
$backupRoot=Get-YfFullPath $BackupDirectory
Assert-YfSeparate @($currentRoot,$currentConfig.Storage,$currentConfig.Path,$backupRoot)
if ($currentConfig.CaFile) { Assert-YfSeparate @($currentConfig.CaFile,$currentRoot,$backupRoot) }
if ($Action -eq 'Restore') {
    if ($MigrateDatabase) { throw 'Restore uses the backed-up application and schema; migration must be a separate later upgrade.' }
    $targetConfig=Read-YfMaintenanceConfig $RestoreConfigPath
    if ($targetConfig.Database -eq $currentConfig.Database) { throw 'Use a different database name for restore.' }
    if ($targetConfig.Origin -ne $currentConfig.Origin -or $targetConfig.Config.App.CookieSecure -ne $true) { throw 'Restore must retain the current HTTPS site origin and secure cookies.' }
    Assert-YfBackupSite $backupRoot $SiteName | Out-Null
    Assert-YfPublishedConfig (Join-Path $backupRoot 'application') -AllowConfigPath
    Get-Command $MySql -ErrorAction Stop | Out-Null
} else {
    $targetConfig=$currentConfig
    Assert-YfEmptyDirectory $backupRoot
    Get-Command $MySqlDump -ErrorAction Stop | Out-Null
}
if ($Action -ne 'Backup') {
    $NewSiteRoot=Get-YfFullPath $NewSiteRoot
    Assert-YfEmptyDirectory $NewSiteRoot
    $paths=@($currentRoot,$currentConfig.Storage,$currentConfig.Path,$backupRoot,$NewSiteRoot)
    if ($currentConfig.CaFile) { Assert-YfSeparate @($currentConfig.CaFile,$currentRoot,$backupRoot,$NewSiteRoot) }
    if ($Action -eq 'Restore') {
        $paths+=@($targetConfig.Storage,$targetConfig.Path)
        if ($targetConfig.CaFile) { Assert-YfSeparate @($targetConfig.CaFile,$currentRoot,$currentConfig.Storage,$currentConfig.Path,$backupRoot,$NewSiteRoot) }
        Assert-YfEmptyDirectory $targetConfig.Storage
    }
    Assert-YfSeparate $paths
}
if ($Action -eq 'Upgrade') {
    $PackageRoot=Get-YfFullPath $PackageRoot
    Assert-YfSeparate @($PackageRoot,$currentRoot,$currentConfig.Storage,$currentConfig.Path,$backupRoot,$NewSiteRoot)
    if ($currentConfig.CaFile) { Assert-YfSeparate @($currentConfig.CaFile,$PackageRoot,$currentRoot,$backupRoot,$NewSiteRoot) }
    Assert-YfManifest $PackageRoot | Out-Null
    Assert-YfPublishedConfig $PackageRoot
    foreach ($required in @('Yf.Api.dll','Yf.Api.runtimeconfig.json','web.config','wwwroot\index.html')) {
        if (!(Test-Path -LiteralPath (Join-Path $PackageRoot $required) -PathType Leaf)) { throw 'Incomplete upgrade package.' }
    }
    if ($MigrateDatabase) { Get-Command dotnet -ErrorAction Stop | Out-Null }
}
if ($MigrateDatabase -and $Action -ne 'Upgrade') { throw 'MigrateDatabase is only supported for Upgrade.' }
$wasRunning=(Get-WebAppPoolState -Name $pool).Value -eq 'Started'
try {
    if ($wasRunning) { Stop-WebAppPool -Name $pool }
    $deadline=[DateTime]::UtcNow.AddSeconds(120)
    while ((Get-WebAppPoolState -Name $pool).Value -ne 'Stopped' -or !(Test-YfWorkersStopped $pool)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Application pool and its workers did not stop within 120 seconds; no backup or restore was started.' }
        Start-Sleep -Milliseconds 200
    }
    if ($Action -eq 'Restore') {
        Restore-YfBackup $backupRoot $targetConfig $NewSiteRoot $MySql
    } else {
        New-YfBackup $currentRoot $currentConfig $backupRoot $MySqlDump $SiteName
        if ($Action -eq 'Upgrade') { Copy-YfTree $PackageRoot $NewSiteRoot }
    }
    if ($Action -ne 'Backup') {
        Set-YfSiteConfig $NewSiteRoot $targetConfig.Path
        Grant-YfApplicationAccess $NewSiteRoot $targetConfig $pool
        if ($MigrateDatabase) { Invoke-YfMigration $NewSiteRoot $targetConfig.Path }
        Set-ItemProperty ('IIS:\Sites\'+$SiteName) -Name physicalPath -Value $NewSiteRoot
    }
    if ($wasRunning) {
        Start-WebAppPool -Name $pool
        $healthy=$false
        for ($attempt=0; $attempt -lt 12; $attempt++) {
            try { $health=Invoke-RestMethod ($targetConfig.Origin.TrimEnd('/')+'/health') -TimeoutSec 5; if ($health.status -eq 'ok' -and $health.db -eq 'up') { $healthy=$true; break } } catch { }
            Start-Sleep -Seconds 1
        }
        if (!$healthy) { throw 'HTTPS health check failed; pool will remain stopped for investigation.' }
    }
    Write-Host "$Action completed for $SiteName. Backup: $backupRoot"
    if (!$wasRunning) { Write-Host 'The application pool was already stopped and remains stopped.' }
} catch {
    if ((Get-WebAppPoolState -Name $pool).Value -ne 'Stopped') { Stop-WebAppPool -Name $pool }
    Write-Warning 'Maintenance failed. Only the named pool was stopped. Old application and original database/storage were not deleted; inspect the failure before resuming.'
    throw
}
