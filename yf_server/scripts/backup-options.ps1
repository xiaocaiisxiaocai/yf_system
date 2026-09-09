function ConvertTo-MySqlOptionValue {
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Value)
    # MySQL option files interpret backslash escapes inside quoted values.
    '"' + $Value.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n').Replace("`t", '\t') + '"'
}

function Test-BackupInsideStorage {
    param([string] $BackupPath, [string] $StoragePath)
    $storagePrefix = $StoragePath.TrimEnd([char[]]'\/') + [System.IO.Path]::DirectorySeparatorChar
    $BackupPath.Equals($StoragePath, [System.StringComparison]::OrdinalIgnoreCase) -or
        $BackupPath.StartsWith($storagePrefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function New-BackupDirectoryName {
    param([datetime] $Time = (Get-Date))
    'yf_system_' + $Time.ToString('yyyyMMdd_HHmmss') + '_' + [guid]::NewGuid().ToString('N')
}
