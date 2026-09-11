#Requires -Version 5.1
# Shared by maintain-iis.ps1; importing this file performs no maintenance.
Set-StrictMode -Version 2.0

function Get-YfFullPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -notmatch '^[A-Za-z]:[\\/]') { throw 'Use an absolute local disk path.' }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    if ($full.Length -le 3) { throw 'Drive roots are not allowed.' }
    return $full
}
function Test-YfWithin([string]$Child, [string]$Parent) {
    return $Child.Equals($Parent,[StringComparison]::OrdinalIgnoreCase) -or $Child.StartsWith($Parent+'\',[StringComparison]::OrdinalIgnoreCase)
}
function Assert-YfNoLinks([string]$Path) {
    $cursor = $Path
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse points are not allowed in maintenance paths.' }
        $cursor = Split-Path -Parent $cursor
    }
    if (Test-Path -LiteralPath $Path -PathType Container) {
        foreach ($item in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'A maintenance tree contains a reparse point.' }
        }
    }
}
function Assert-YfSeparate([string[]]$Paths) {
    for ($i=0; $i -lt $Paths.Count; $i++) {
        for ($j=$i+1; $j -lt $Paths.Count; $j++) {
            if ((Test-YfWithin $Paths[$i] $Paths[$j]) -or (Test-YfWithin $Paths[$j] $Paths[$i])) { throw 'Maintenance paths must not overlap.' }
        }
    }
}
function Assert-YfEmptyDirectory([string]$Path) {
    Assert-YfNoLinks $Path
    if (Test-Path -LiteralPath $Path) {
        if (!(Test-Path -LiteralPath $Path -PathType Container) -or @(Get-ChildItem -LiteralPath $Path -Force).Count) { throw 'Destination must be absent or an empty directory.' }
    }
}
function Protect-YfDirectory([string]$Path) {
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    $sids = @([Security.Principal.WindowsIdentity]::GetCurrent().User, (New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'), (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'))
    foreach ($sid in $sids) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Write-YfJson([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 12),(New-Object Text.UTF8Encoding($false)))
}
function Get-YfDbOption($Builder,[string[]]$Names,[string]$Default='') {
    foreach ($name in $Names) { if ($Builder.ContainsKey($name)) { return [string]$Builder[$name] } }
    return $Default
}
function Read-YfMaintenanceConfig([string]$Path) {
    $Path = Get-YfFullPath $Path
    Assert-YfNoLinks $Path
    try {
        $config = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        $builder = New-Object System.Data.Common.DbConnectionStringBuilder
        $builder.set_ConnectionString($config.App.ConnectionString)
        $storage = Get-YfFullPath $config.App.StorageRoot
        $database = Get-YfDbOption $builder @('Database','Initial Catalog')
        $server = Get-YfDbOption $builder @('Server','Host','Data Source','DataSource','Address','Addr','Network Address') 'localhost'
        $port = [int](Get-YfDbOption $builder @('Port') '3306')
        $user = Get-YfDbOption $builder @('User ID','UserID','User Id','User','Uid','Username')
        $password = Get-YfDbOption $builder @('Password','Pwd')
        $protocol = Get-YfDbOption $builder @('Connection Protocol','ConnectionProtocol','Protocol') 'Sockets'
        $ssl = Get-YfDbOption $builder @('SSL Mode','SslMode') 'Preferred'
        $sslModes = @{ None='DISABLED'; Disabled='DISABLED'; Preferred='PREFERRED'; Required='REQUIRED'; VerifyCA='VERIFY_CA'; VerifyFull='VERIFY_IDENTITY' }
        if ($database -notmatch '^[A-Za-z0-9_]+$' -or !$user -or $server -notmatch '^[A-Za-z0-9_.:-]+$' -or $port -lt 1 -or $port -gt 65535 -or $protocol -notin @('Sockets','Socket','TCP','Tcp') -or !$sslModes.ContainsKey($ssl)) { throw 'Unsupported database settings.' }
        foreach ($key in @('CertificateFile','Certificate File','CertificatePassword','Certificate Password','SslCert','SslKey','SslCa','CACertificateFile')) {
            if ($builder.ContainsKey($key)) { throw 'Client certificate connections need a separately configured backup client.' }
        }
        $origin = [Uri]$config.App.WebBaseUrl
        if (!$origin.IsAbsoluteUri -or $origin.Scheme -notin @('http','https')) { throw 'Invalid origin.' }
    } catch { throw 'Unable to read maintenance configuration. Check paths, TCP database settings and JSON; credentials are not printed.' }
    return [pscustomobject]@{ Path=$Path; Storage=$storage; Database=$database; Server=$server; Port=$port; User=$user; Password=$password; SslMode=$sslModes[$ssl]; Origin=$config.App.WebBaseUrl; Config=$config }
}
function ConvertTo-YfMySqlOption([string]$Value) {
    return '"'+$Value.Replace('\','\\').Replace('"','\"').Replace("`r",'\r').Replace("`n",'\n')+'"'
}
function Write-YfMySqlDefaults($Config,[string]$Directory) {
    $path = Join-Path $Directory ('.mysql-'+[guid]::NewGuid().ToString('N')+'.cnf')
    $lines = @('[client]', ('host='+(ConvertTo-YfMySqlOption $Config.Server)), ('port='+$Config.Port), ('user='+(ConvertTo-YfMySqlOption $Config.User)), ('password='+(ConvertTo-YfMySqlOption $Config.Password)), ('ssl-mode='+$Config.SslMode), 'protocol=TCP', 'default-character-set=utf8mb4')
    try {
        [IO.File]::WriteAllLines($path,$lines,(New-Object Text.UTF8Encoding($false)))
        return $path
    } catch {
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
        throw
    }
}
function Assert-YfTreeBytes([string]$Source,[string]$Destination) {
    Assert-YfNoLinks $Source
    Assert-YfNoLinks $Destination
    $sourceFiles = @(Get-ChildItem -LiteralPath $Source -Recurse -File -Force)
    $destinationFiles = @(Get-ChildItem -LiteralPath $Destination -Recurse -File -Force)
    if ($sourceFiles.Count -ne $destinationFiles.Count) { throw 'File copy verification failed: source and destination file counts differ.' }
    foreach ($sourceFile in $sourceFiles) {
        $relative = $sourceFile.FullName.Substring($Source.Length).TrimStart('\')
        $destinationFile = Join-Path $Destination $relative
        if (!(Test-Path -LiteralPath $destinationFile -PathType Leaf)) { throw 'File copy verification failed: a destination file is missing.' }
        if ($sourceFile.Length -ne (Get-Item -LiteralPath $destinationFile -Force).Length) { throw 'File copy verification failed: file lengths differ.' }
        if ((Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $destinationFile -Algorithm SHA256).Hash) { throw 'File copy verification failed: file hashes differ.' }
    }
}
function Copy-YfTree([string]$Source,[string]$Destination) {
    Assert-YfNoLinks $Source
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    & robocopy.exe $Source $Destination /E /COPY:DAT /DCOPY:DAT /XJ /R:1 /W:1 /NP /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -ge 8) { throw 'File copy failed; original files are unchanged.' }
    Assert-YfTreeBytes $Source $Destination
}
function Get-YfManifestFiles([string]$Root) {
    $result = @()
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Sort-Object FullName) {
        $relative = $file.FullName.Substring($Root.Length).TrimStart('\').Replace('\','/')
        if ($relative -eq 'manifest.json') { continue }
        $result += [ordered]@{path=$relative;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant();bytes=$file.Length}
    }
    return $result
}
function Assert-YfManifest([string]$Root,[string]$Kind='') {
    Assert-YfNoLinks $Root
    $manifest = Get-Content -LiteralPath (Join-Path $Root 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($Kind -and $manifest.kind -ne $Kind) { throw 'Incorrect backup manifest kind.' }
    if ($Kind -eq 'yf-offline-backup' -and $manifest.schemaVersion -ne 1) { throw 'Unsupported backup manifest schema version.' }
    $known = @{}
    foreach ($entry in $manifest.files) {
        if ([IO.Path]::IsPathRooted($entry.path) -or $entry.path -match '(^|[\/])\.\.([\/]|$)' -or $entry.path.Contains(':')) { throw 'Unsafe manifest path.' }
        $file = [IO.Path]::GetFullPath((Join-Path $Root $entry.path))
        $relative = $file.Substring($Root.Length).TrimStart('\').Replace('\','/').ToLowerInvariant()
        if (!(Test-YfWithin $file $Root) -or $known.ContainsKey($relative) -or !(Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Invalid or duplicate manifest member.' }
        if ((Get-Item -LiteralPath $file -Force).Length -ne $entry.bytes) { throw 'Manifest file length mismatch.' }
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { throw 'Manifest hash mismatch.' }
        $known[$relative] = $true
    }
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File -Force) {
        $relative = $file.FullName.Substring($Root.Length).TrimStart('\').Replace('\','/').ToLowerInvariant()
        if ($relative -ne 'manifest.json' -and !$known.ContainsKey($relative)) { throw 'Unlisted file in manifest.' }
    }
    return $manifest
}
function Assert-YfBackupSite([string]$BackupRoot,[string]$SiteName) {
    $manifest = Assert-YfManifest $BackupRoot 'yf-offline-backup'
    if ([string]::IsNullOrWhiteSpace($SiteName) -or
        ![string]::Equals([string]$manifest.siteName,$SiteName,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Backup belongs to a different IIS site.'
    }
    return $manifest
}
function New-YfBackup([string]$ApplicationRoot,$Config,[string]$Destination,[string]$MySqlDump,[string]$SiteName) {
    $ApplicationRoot = Get-YfFullPath $ApplicationRoot
    $Destination = Get-YfFullPath $Destination
    Assert-YfSeparate @($ApplicationRoot,$Config.Storage,$Config.Path,$Destination)
    Assert-YfEmptyDirectory $Destination
    Assert-YfNoLinks $ApplicationRoot
    Assert-YfNoLinks $Config.Storage
    if (!(Test-Path -LiteralPath $Config.Storage -PathType Container)) { throw 'Storage directory is missing.' }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Protect-YfDirectory $Destination
    $defaults = $null
    try {
        $defaults = Write-YfMySqlDefaults $Config $Destination
        $sql = Join-Path $Destination 'database.sql'
        & $MySqlDump "--defaults-file=$defaults" --single-transaction --routines --triggers --events --hex-blob --no-tablespaces "--result-file=$sql" $Config.Database 2> (Join-Path $Destination 'dump.stderr.log')
        if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $sql) -or (Get-Item -LiteralPath $sql).Length -eq 0) { throw 'Database backup failed. Inspect the protected backup log.' }
    } finally {
        if ($defaults -and (Test-Path -LiteralPath $defaults -PathType Leaf)) { Remove-Item -LiteralPath $defaults -Force }
    }
    Copy-YfTree $ApplicationRoot (Join-Path $Destination 'application')
    Copy-YfTree $Config.Storage (Join-Path $Destination 'storage')
    Copy-Item -LiteralPath $Config.Path -Destination (Join-Path $Destination 'configuration.json')
    $manifest = [ordered]@{kind='yf-offline-backup';schemaVersion=1;createdUtc=[DateTime]::UtcNow.ToString('o');siteName=$SiteName;database=$Config.Database;files=@(Get-YfManifestFiles $Destination)}
    Write-YfJson (Join-Path $Destination 'manifest.json') $manifest
    Assert-YfManifest $Destination 'yf-offline-backup' | Out-Null
}
function Restore-YfBackup([string]$BackupRoot,$Config,[string]$NewApplicationRoot,[string]$MySql) {
    $BackupRoot = Get-YfFullPath $BackupRoot
    $NewApplicationRoot = Get-YfFullPath $NewApplicationRoot
    $manifest = Assert-YfManifest $BackupRoot 'yf-offline-backup'
    foreach ($name in @('database.sql','configuration.json','application\Yf.Api.dll','application\web.config','application\wwwroot\index.html')) {
        if (!(Test-Path -LiteralPath (Join-Path $BackupRoot $name) -PathType Leaf)) { throw 'Required backup payload is missing.' }
    }
    Assert-YfSeparate @($BackupRoot,$Config.Storage,$Config.Path,$NewApplicationRoot)
    Assert-YfEmptyDirectory $NewApplicationRoot
    Assert-YfEmptyDirectory $Config.Storage
    $scratch = Join-Path ([IO.Path]::GetTempPath()) ('yf-restore-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scratch | Out-Null
    Protect-YfDirectory $scratch
    $defaults = $null
    try {
        $defaults = Write-YfMySqlDefaults $Config $scratch
        $count = @(& $MySql "--defaults-file=$defaults" --batch --skip-column-names "--database=$($Config.Database)" '--execute=SELECT (SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE())+(SELECT COUNT(*) FROM information_schema.routines WHERE routine_schema=DATABASE())+(SELECT COUNT(*) FROM information_schema.events WHERE event_schema=DATABASE())' 2> (Join-Path $scratch 'probe.stderr.log'))
        if ($LASTEXITCODE -ne 0 -or $count.Count -ne 1 -or $count[0].Trim() -ne '0') { throw 'Restore requires an existing empty database; no data was overwritten.' }
        # Stream SQL bytes without shell parsing or embedding credentials in arguments.
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = (Get-Command $MySql -ErrorAction Stop).Source
        $start.Arguments = '"--defaults-file='+$defaults+'" --binary-mode=1 "--database='+$Config.Database+'"'
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardInput = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $process = New-Object Diagnostics.Process
        $process.StartInfo = $start
        try {
            if (!$process.Start()) { throw 'Unable to start database restore.' }
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            $inputFile = [IO.File]::OpenRead((Join-Path $BackupRoot 'database.sql'))
            try { $inputFile.CopyTo($process.StandardInput.BaseStream) } finally { $inputFile.Dispose(); $process.StandardInput.Close() }
            $process.WaitForExit()
            $null = $stdout.GetAwaiter().GetResult()
            $null = $stderr.GetAwaiter().GetResult()
            if ($process.ExitCode -ne 0) { throw 'Database import failed. The new database may be partial; the original database is unchanged.' }
        } finally { $process.Dispose() }
        Copy-YfTree (Join-Path $BackupRoot 'storage') $Config.Storage
        Copy-YfTree (Join-Path $BackupRoot 'application') $NewApplicationRoot
    } finally {
        if ($defaults -and (Test-Path -LiteralPath $defaults -PathType Leaf)) { Remove-Item -LiteralPath $defaults -Force }
        # Only exact owned scratch files; no recursive cleanup of caller paths.
        foreach ($file in Get-ChildItem -LiteralPath $scratch -File) { Remove-Item -LiteralPath $file.FullName -Force }
        Remove-Item -LiteralPath $scratch
    }
}
