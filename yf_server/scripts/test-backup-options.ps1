$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'backup-options.ps1')
$cases = @(
    @{ Input = 'abc\t123'; Expected = '"abc\\t123"' },
    @{ Input = 'a"b#c;d'; Expected = '"a\"b#c;d"' },
    @{ Input = "line`nbreak`ttab`rreturn"; Expected = '"line\nbreak\ttab\rreturn"' },
    @{ Input = ' space '; Expected = '" space "' },
    @{ Input = ''; Expected = '""' }
)
foreach ($case in $cases) {
    if ((ConvertTo-MySqlOptionValue $case.Input) -cne $case.Expected) { throw 'MySQL option escaping failed' }
}
if (Test-BackupInsideStorage 'D:\storage-backups' 'D:\storage') { throw 'Sibling backup path rejected' }
if (-not (Test-BackupInsideStorage 'D:\storage\backup' 'D:\storage')) { throw 'Nested backup path allowed' }
if (-not (Test-BackupInsideStorage 'D:\storage' 'D:\storage')) { throw 'Identical backup path allowed' }
if (-not (Test-BackupInsideStorage 'D:\storage\backup' 'D:\storage\')) { throw 'Trailing separator check failed' }
$fixedTime = [datetime]'2026-09-09T12:00:00'
$firstBackup = New-BackupDirectoryName $fixedTime
$secondBackup = New-BackupDirectoryName $fixedTime
if ($firstBackup -ceq $secondBackup) { throw 'Concurrent backups reuse their destination' }
if ($firstBackup -notmatch '^yf_system_20260909_120000_[0-9a-f]{32}$') { throw 'Unexpected backup directory format' }
Write-Output '11 backup option/path checks passed; no database or files copied.'
