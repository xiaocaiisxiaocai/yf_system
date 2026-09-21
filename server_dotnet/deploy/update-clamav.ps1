#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
Run an immediate trusted ClamAV signature update for the bundled installation.
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = 'C:\Program Files\YfSystem\ClamAV'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ($InstallRoot -notmatch '^[A-Za-z]:[\\/]') { throw 'InstallRoot must be an absolute local disk path.' }
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\', '/')
if ($InstallRoot.Length -le 3) { throw 'InstallRoot cannot be a drive root.' }
$freshclamExe = @(Get-ChildItem -LiteralPath $InstallRoot -Recurse -File -Filter 'freshclam.exe' -ErrorAction Stop)
if ($freshclamExe.Count -ne 1) { throw 'Expected exactly one bundled freshclam.exe below InstallRoot.' }
$configPath = Join-Path $freshclamExe[0].DirectoryName 'freshclam.conf'
if (!(Test-Path -LiteralPath $configPath -PathType Leaf)) { throw 'freshclam.conf is missing from the installed program directory.' }

$service = Get-CimInstance Win32_Service -Filter "Name='freshclam'" -ErrorAction Stop
$expectedPrefix = '"' + $freshclamExe[0].FullName + '"'
if (![string]$service.PathName -or !$service.PathName.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The freshclam service does not belong to this InstallRoot; it will not be stopped or changed.'
}
$wasRunning = $service.State -eq 'Running'
$updated = $false
if ($wasRunning) { Stop-Service -Name 'freshclam' -ErrorAction Stop }
try {
    $arguments = @('--config-file', '"' + $configPath + '"', '--show-progress')
    $process = Start-Process -FilePath $freshclamExe[0].FullName -ArgumentList $arguments -WorkingDirectory $freshclamExe[0].DirectoryName -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "FreshClam exited with code $($process.ExitCode)." }
    $updated = $true
}
finally {
    if ($wasRunning -or $updated) { Start-Service -Name 'freshclam' }
}
Write-Host 'ClamAV signature database update completed; NotifyClamd requested a reload.'
