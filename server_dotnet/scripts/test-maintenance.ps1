#Requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$FixtureRoot)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\deploy\maintenance-common.ps1')
$root=Get-YfFullPath $FixtureRoot
Protect-YfDirectory $root
function Reject([scriptblock]$Operation,[string]$Name) {
    $rejected=$false
    try { & $Operation | Out-Null } catch { $rejected=$true }
    if (!$rejected) { throw "Expected rejection: $Name" }
    Write-Output "PASS $Name"
}
$maintainScript=Join-Path $PSScriptRoot '..\deploy\maintain-iis.ps1'
$tokens=$null
$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($maintainScript,[ref]$tokens,[ref]$parseErrors)
if ($parseErrors.Count) { throw 'Unable to parse maintain-iis.ps1 guard functions.' }
foreach ($name in @('Assert-YfEnvironmentNames','Assert-YfLaunch','Assert-YfPublishedConfig')) {
    $definitions=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$true) | Where-Object { $_.Name -eq $name })
    if ($definitions.Count -ne 1) { throw "Expected exactly one guard function: $name" }
    Invoke-Expression $definitions[0].Extent.Text
}
$source=Read-YfMaintenanceConfig (Join-Path $root 'source.json')
$target=Read-YfMaintenanceConfig (Join-Path $root 'target.json')
$second=Read-YfMaintenanceConfig (Join-Path $root 'second.json')
$guarded=Read-YfMaintenanceConfig (Join-Path $root 'guarded.json')
$backup=Join-Path $root 'backup'
$application=Join-Path $root 'application'
$restored=Join-Path $root 'restored-app'
$sqlClient=(Get-Command mysql.exe -ErrorAction Stop).Source
$dumpClient=(Get-Command mysqldump.exe -ErrorAction Stop).Source
Reject { Assert-YfSeparate @($application,(Join-Path $application 'nested')) } 'overlapping paths refused'
Assert-YfLaunch 'dotnet' '.\Yf.Api.dll' 'inprocess'
Assert-YfEnvironmentNames @('YF_CONFIG_PATH','ASPNETCORE_ENVIRONMENT') -AllowConfigPath
Assert-YfPublishedConfig $application
Write-Output 'PASS standard dotnet in-process published configuration guard'
$webConfigPath=Join-Path $application 'web.config'
$standardWebConfig=[IO.File]::ReadAllBytes($webConfigPath)
try {
    [xml]$xml=Get-Content -LiteralPath $webConfigPath -Raw -Encoding UTF8
    $xml.SelectSingleNode('//aspNetCore').SetAttribute('arguments','.\Yf.Api.dll --urls http://127.0.0.1:5000')
    $xml.Save($webConfigPath)
    Reject { Assert-YfPublishedConfig $application } 'additional launch command line refused'
} finally { [IO.File]::WriteAllBytes($webConfigPath,$standardWebConfig) }
foreach ($variableName in @('App__ConnectionString','App:StorageRoot')) {
    try {
        [xml]$xml=Get-Content -LiteralPath $webConfigPath -Raw -Encoding UTF8
        $variables=$xml.SelectSingleNode('//aspNetCore/environmentVariables')
        $node=$xml.CreateElement('environmentVariable')
        $node.SetAttribute('name',$variableName)
        $node.SetAttribute('value','forbidden-test-value')
        $variables.AppendChild($node) | Out-Null
        $xml.Save($webConfigPath)
        Reject { Assert-YfPublishedConfig $application } "$variableName published override refused"
    } finally { [IO.File]::WriteAllBytes($webConfigPath,$standardWebConfig) }
}
Reject { Assert-YfEnvironmentNames @('YF_CONFIG_PATH') } 'inherited YF_CONFIG_PATH refused'
Write-Output 'PASS only extracted guards were exercised; IIS was not used'
$missingDefaultsParent=Join-Path $root 'missing-defaults-parent'
Reject { Write-YfMySqlDefaults $source $missingDefaultsParent } 'credential defaults write failure handled'
if (Test-Path -LiteralPath $missingDefaultsParent) { throw 'Credential defaults failure created an unexpected path.' }
New-YfBackup $application $source $backup $dumpClient 'isolated-test'
Write-Output 'PASS actual MySQL and storage backup'
$ownedManifest=Assert-YfBackupSite $backup 'ISOLATED-TEST'
if ($ownedManifest.siteName -ne 'isolated-test') { throw 'Site ownership guard did not return the validated manifest.' }
Reject { Assert-YfBackupSite $backup 'different-site' } 'backup from a different site refused'
$manifestPath=Join-Path $backup 'manifest.json'
$originalManifest=[IO.File]::ReadAllBytes($manifestPath)
try {
    $unsupported=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $unsupported.schemaVersion=2
    Write-YfJson $manifestPath $unsupported
    Reject { Assert-YfManifest $backup 'yf-offline-backup' } 'unsupported backup manifest schema refused'
} finally { [IO.File]::WriteAllBytes($manifestPath,$originalManifest) }
Reject { New-YfBackup $application $source $backup $dumpClient 'isolated-test' } 'existing backup refused'
if (@(Get-ChildItem -LiteralPath $backup -Filter '*.cnf').Count) { throw 'Credential file remains after backup.' }
$sql=Join-Path $backup 'database.sql'
$original=[IO.File]::ReadAllBytes($sql)
try {
    [IO.File]::AppendAllText($sql,'tamper')
    Reject { Restore-YfBackup $backup $target $restored $sqlClient } 'tampered backup refused before import'
} finally { [IO.File]::WriteAllBytes($sql,$original) }
$extra=Join-Path $backup 'unlisted.txt'
try {
    [IO.File]::WriteAllText($extra,'unlisted')
    Reject { Assert-YfManifest $backup 'yf-offline-backup' } 'unlisted backup file refused'
} finally { Remove-Item -LiteralPath $extra }
Reject { Restore-YfBackup $backup $guarded (Join-Path $root 'guarded-app') $sqlClient } 'tableless schema procedure and event refused'
if ((Test-Path -LiteralPath (Join-Path $root 'guarded-app')) -or (Test-Path -LiteralPath $guarded.Storage)) { throw 'Rejected tableless-schema restore created application or storage files.' }
Restore-YfBackup $backup $target $restored $sqlClient
Write-Output 'PASS restore into new empty database and directories'
Reject { Restore-YfBackup $backup $second (Join-Path $root 'second-app') $sqlClient } 'nonempty database refused'
if (Test-Path -LiteralPath (Join-Path $root 'second-app')) { throw 'Rejected restore created application files.' }
foreach ($relative in @('wwwroot\index.html','Yf.Api.dll','web.config')) {
    if ((Get-FileHash -LiteralPath (Join-Path $application $relative)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $restored $relative)).Hash) { throw 'Application restore hash mismatch.' }
}
foreach ($file in Get-ChildItem -LiteralPath $source.Storage -File -Recurse) {
    $relative=$file.FullName.Substring($source.Storage.Length).TrimStart('\')
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $target.Storage $relative)).Hash) { throw 'Storage restore hash mismatch.' }
}
Write-Output 'PASS application and storage bytes preserved'
