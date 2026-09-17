#Requires -Version 5.1
# Filesystem-only tests: no IIS, service, database or certificate writes.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$source = Join-Path $repo 'yf_server\deploy\install-iis.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Installer parse failed' }
foreach ($function in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    . ([scriptblock]::Create($function.Extent.Text))
}
$script:checks = 0
function Assert-True($Condition, [string]$Label) {
    if (-not $Condition) { throw $Label }
    $script:checks++
}
function Assert-Rejected([scriptblock]$Operation) {
    $rejected = $false
    try { & $Operation | Out-Null } catch { $rejected = $true }
    Assert-True $rejected 'Expected operation to be rejected'
}
# 旧入口必须先于依赖安装、构建、输出目录创建拒绝执行。
$retiredOutput = Join-Path $repo ('.runlogs\retired-release-' + [guid]::NewGuid().ToString('N'))
Assert-Rejected { & (Join-Path $repo 'yf_server\scripts\deploy-iis.ps1') -OutputDir $retiredOutput -AllowDirty -SkipChecks }
Assert-True (-not (Test-Path -LiteralPath $retiredOutput)) 'Retired publisher wrote output'
$fixture = Join-Path $repo ('.runlogs\iis-script-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    Assert-Rejected { Resolve-FullPath 'C:relative' }
    Assert-Rejected { Assert-SafeDeployRoot 'C:\' 'test' }
    Assert-Rejected { Assert-SafeDeployRoot $env:windir 'test' }
    Assert-Rejected { Assert-DisjointPaths @('C:\apps\site', 'C:\apps\site\backup') }
    Assert-Rejected { Assert-DisjointPaths @('C:\apps\site\backup', 'C:\apps\site') }
    Assert-DisjointPaths @('C:\apps\site', 'C:\apps\server', 'C:\apps\data')
    $script:checks++
    $config = "[server]<NL>addr = '127.0.0.1:8080' # comment<NL>[jwt]<NL>cookie_secure = true<NL>".Replace('<NL>', [Environment]::NewLine)
    Assert-True ((Read-DeployScalar $config server addr) -eq '127.0.0.1:8080') 'Section-scoped value failed'
    Assert-Rejected { Read-DeployScalar $config server cookie_secure }
    Assert-Rejected { Read-DeployScalar ($config + "[server]<NL>addr='other'".Replace('<NL>', [Environment]::NewLine)) server addr }

    $pkg = Join-Path $fixture 'package'
    New-Item -ItemType Directory -Path $pkg | Out-Null
    $hashes = @()
    for ($n = 0; $n -lt 8; $n++) {
        $path = Join-Path $pkg "$n.txt"
        [IO.File]::WriteAllText($path, "original-$n")
        $hashes += ((Get-FileHash -LiteralPath $path).Hash + "  $n.txt")
    }
    [IO.File]::WriteAllLines((Join-Path $pkg 'SHA256SUMS.txt'), [string[]]$hashes)
    Assert-True ((Test-PackageHashes $pkg) -eq 8) 'Valid package rejected'
    [IO.File]::WriteAllText((Join-Path $pkg '0.txt'), 'tampered')
    Assert-Rejected { Test-PackageHashes $pkg }
    [IO.File]::WriteAllText((Join-Path $pkg '0.txt'), 'original-0')
    [IO.File]::WriteAllText((Join-Path $pkg 'unlisted.txt'), 'unlisted')
    Assert-Rejected { Test-PackageHashes $pkg }
    $copy = Join-Path $fixture 'copy'
    Copy-DirectoryContents $pkg $copy
    Assert-DirectoryCopy $pkg $copy
    $script:checks++
    [IO.File]::WriteAllText((Join-Path $copy '1.txt'), 'partial backup')
    Assert-Rejected { Assert-DirectoryCopy $pkg $copy }

    # Exercise the real top-level failure handler at the point of a backup failure.
    # Any regression that deletes old directories before a backup exists destroys
    # these sentinels, even though all service and IIS access is absent.
    $SiteRoot = Join-Path $fixture 'old-site'
    $ServerRoot = Join-Path $fixture 'old-server'
    New-Item -ItemType Directory -Path $SiteRoot,$ServerRoot | Out-Null
    [IO.File]::WriteAllText((Join-Path $SiteRoot 'sentinel'), 'site-original')
    [IO.File]::WriteAllText((Join-Path $ServerRoot 'sentinel'), 'server-original')
    $filesChanged = $false
    $migrationStarted = $false
    $siteWasStarted = $false
    $serviceWasRunning = $false
    $existingService = $null
    $ServiceName = 'NeverInstalled'
    $handler = @($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] })[-1].CatchClauses[0].Body
    $handlerText = $handler.Extent.Text
    $body = [scriptblock]::Create($handlerText.Substring(1, $handlerText.Length - 2))
    try { throw 'simulated backup failure' } catch {
        try { & $body } catch { }
    }
    Assert-True (([IO.File]::ReadAllText((Join-Path $SiteRoot 'sentinel'))) -eq 'site-original') 'Backup failure destroyed site'
    Assert-True (([IO.File]::ReadAllText((Join-Path $ServerRoot 'sentinel'))) -eq 'server-original') 'Backup failure destroyed backend'
    Write-Output "$script:checks IIS deployment filesystem checks passed; no IIS/service/database writes."
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $allowed = [IO.Path]::GetFullPath((Join-Path $repo '.runlogs')) + '\'
    if ($resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolved) -like 'iis-script-test-*') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
