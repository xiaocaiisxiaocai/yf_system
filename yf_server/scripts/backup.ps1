[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BackupRoot,
    [Parameter(Mandatory)] [string] $StorageRoot,
    [Parameter(Mandatory)] [string] $DatabaseName,
    [string] $DatabaseHost = "127.0.0.1",
    [ValidateRange(1, 65535)] [int] $DatabasePort = 3306,
    [Parameter(Mandatory)] [string] $DatabaseUser,
    [string] $MySqlDump = "mysqldump.exe"
)

$ErrorActionPreference = "Stop"

if (-not $env:YF_BACKUP_DB_PASSWORD) {
    throw "请通过进程级环境变量 YF_BACKUP_DB_PASSWORD 提供数据库密码。"
}

$storagePath = [System.IO.Path]::GetFullPath($StorageRoot)
$backupBase = [System.IO.Path]::GetFullPath($BackupRoot)
if (-not (Test-Path -LiteralPath $storagePath -PathType Container)) {
    throw "存储目录不存在：$storagePath"
}
if ($backupBase.StartsWith($storagePath, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "备份目录不能位于存储目录内部。"
}

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$destination = Join-Path $backupBase "yf_system_$stamp"
$databaseFile = Join-Path $destination "database.sql"
$databaseError = Join-Path $destination "mysqldump.stderr.log"
$storageDestination = Join-Path $destination "storage"
$defaultsFile = Join-Path ([System.IO.Path]::GetTempPath()) "yf_mysqldump_$([guid]::NewGuid().ToString('N')).cnf"

New-Item -ItemType Directory -Path $destination -Force | Out-Null
New-Item -ItemType Directory -Path $storageDestination -Force | Out-Null

try {
    $escapedPassword = $env:YF_BACKUP_DB_PASSWORD.Replace("\\", "\\\\").Replace('"', '\"')
    [System.IO.File]::WriteAllText(
        $defaultsFile,
        "[client]`r`nuser=$DatabaseUser`r`npassword=`"$escapedPassword`"`r`nhost=$DatabaseHost`r`nport=$DatabasePort`r`n",
        [System.Text.UTF8Encoding]::new($false)
    )

    $dumpArgs = @(
        "--defaults-extra-file=$defaultsFile",
        "--single-transaction",
        "--routines",
        "--triggers",
        "--events",
        "--hex-blob",
        "--default-character-set=utf8mb4",
        $DatabaseName
    )
    $dump = Start-Process -FilePath $MySqlDump -ArgumentList $dumpArgs -Wait -PassThru -NoNewWindow `
        -RedirectStandardOutput $databaseFile -RedirectStandardError $databaseError
    if ($dump.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $databaseFile) -or (Get-Item -LiteralPath $databaseFile).Length -eq 0) {
        throw "数据库备份失败（退出码 $($dump.ExitCode)），请检查 $databaseError"
    }

    & robocopy.exe $storagePath $storageDestination /E /COPY:DAT /DCOPY:DAT /R:2 /W:2 /NP /NFL /NDL
    $robocopyExit = $LASTEXITCODE
    if ($robocopyExit -ge 8) {
        throw "存储文件备份失败（robocopy 退出码 $robocopyExit）。"
    }

    $hashes = Get-ChildItem -LiteralPath $destination -File -Recurse |
        Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
        ForEach-Object {
            $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
            $relative = [System.IO.Path]::GetRelativePath($destination, $_.FullName)
            "$($hash.Hash)  $relative"
        }
    [System.IO.File]::WriteAllLines((Join-Path $destination "SHA256SUMS.txt"), $hashes)
    Write-Host "备份完成：$destination"
}
finally {
    if (Test-Path -LiteralPath $defaultsFile) {
        Remove-Item -LiteralPath $defaultsFile -Force
    }
}
