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

function Get-BackupRelativePath {
    param([Parameter(Mandatory)][string] $BasePath, [Parameter(Mandatory)][string] $ChildPath)
    $base = [System.IO.Path]::GetFullPath($BasePath).TrimEnd([char[]]'\/') + [System.IO.Path]::DirectorySeparatorChar
    $child = [System.IO.Path]::GetFullPath($ChildPath)
    $baseUri = New-Object System.Uri($base)
    $childUri = New-Object System.Uri($child)
    [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($childUri).ToString()).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
}
