#Requires -Version 5.1
<#
.SYNOPSIS
    Runs the local, non-browser verification gates for YF System.

.DESCRIPTION
    The script is cwd-independent and writes one immutable run directory below
    .artifacts/tests/verify-all.  It never reads appsettings.Local.json or user
    secrets.  Database-backed .NET tests and the HTTP contract suite run only
    when the caller explicitly supplies a local YF_TEST_DATABASE_URL.

    HTTP validation is enabled by default when YF_TEST_DATABASE_URL is present.
    Use -SkipHttp when only the build and unit-test gates are required.  The
    real MySQL maintenance suite is opt-in with -IncludeMaintenance.  Browser
    automation is intentionally not part of this script.
#>
[CmdletBinding()]
param(
    [switch]$SkipHttp,
    [switch]$IncludeMaintenance
)

$ErrorActionPreference = 'Stop'

$scriptFile = [IO.Path]::GetFullPath($MyInvocation.MyCommand.Path)
$scriptDirectory = Split-Path -Parent $scriptFile
$serverRoot = [IO.Path]::GetFullPath((Join-Path $scriptDirectory '..'))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $serverRoot '..'))
$webRoot = Join-Path $repositoryRoot 'web'
$artifactRoot = Join-Path $repositoryRoot '.artifacts\tests\verify-all'

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$runId = '{0}-{1}' -f ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$runRoot = Join-Path $artifactRoot $runId
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$reportPath = Join-Path $runRoot 'summary.json'

$script:Results = New-Object 'System.Collections.Generic.List[object]'
$script:PlanNames = @(
    'openapi-update-guard',
    'database-configuration',
    'dotnet-sdk',
    'restore-api',
    'restore-testhost',
    'restore-tests',
    'build-api',
    'build-testhost',
    'build-tests',
    'dotnet-test',
    'ef-model-check',
    'http-isolated',
    'maintenance',
    'npm-ci',
    'npm-lint',
    'npm-test',
    'npm-build'
)
$script:RedactionValues = New-Object 'System.Collections.Generic.List[string]'
$script:DotnetSdkVersion = $null
$script:TestSummary = $null
$script:TestResultsPath = Join-Path $runRoot 'dotnet-test.trx'
$script:DatabaseMode = $null
$script:FailureMessage = $null
$script:ExitCode = 0

function Add-RedactionValue {
    param([AllowNull()][string]$Value)

    if (-not [string]::IsNullOrEmpty($Value) -and -not $script:RedactionValues.Contains($Value)) {
        [void]$script:RedactionValues.Add($Value)
    }
}

function Protect-Text {
    param([AllowNull()][string]$Text)

    if ($null -eq $Text) { return '' }
    $protected = $Text
    foreach ($value in $script:RedactionValues) {
        if (-not [string]::IsNullOrEmpty($value)) {
            $protected = $protected.Replace($value, '[REDACTED]')
        }
    }
    return $protected
}

function Write-Log {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [AllowNull()][string]$Text
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, (Protect-Text $Text), $encoding)
}

function Add-Result {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][ValidateSet('passed', 'failed', 'skipped')][string]$Status,
        [AllowNull()][int]$ExitCode,
        [AllowNull()][string]$LogPath,
        [AllowNull()][string]$Reason,
        [AllowNull()][hashtable]$Details
    )

    $record = [ordered]@{
        name = $Name
        status = $Status
        exitCode = $ExitCode
        logPath = $LogPath
        durationSeconds = $null
        reason = $Reason
    }
    if ($null -ne $Details) { $record.details = $Details }
    [void]$script:Results.Add($record)

    $label = $Status.ToUpperInvariant()
    Write-Host ('[{0}] {1}' -f $label, $Name)
    return $record
}

function Add-SkippedResult {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Reason
    )

    return Add-Result -Name $Name -Status 'skipped' -ExitCode $null -LogPath $null -Reason $Reason -Details $null
}

function Invoke-VerificationStep {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    $logPath = Join-Path $runRoot (($Name -replace '[^A-Za-z0-9_.-]', '_') + '.log')
    $started = [DateTime]::UtcNow
    $output = @()
    $exitCode = 1
    $status = 'failed'
    $reason = 'Command did not complete.'

    try {
        if ($null -eq (Get-Command $FilePath -ErrorAction SilentlyContinue)) {
            throw ('Executable was not found on PATH: ' + $FilePath)
        }
        Push-Location -LiteralPath $WorkingDirectory
        try {
            # Windows PowerShell 5.1 promotes native stderr records to
            # terminating errors when ErrorActionPreference is Stop.  Keep
            # both streams for the log and decide success from the exit code.
            $previousErrorAction = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $output = @(& $FilePath @Arguments 2>&1)
            } finally {
                $ErrorActionPreference = $previousErrorAction
            }
            if ($null -eq $LASTEXITCODE) {
                $exitCode = 0
            } else {
                $exitCode = [int]$LASTEXITCODE
            }
        } finally {
            Pop-Location
        }

        $lines = @($output | ForEach-Object { [string]$_ })
        $commandLine = '{0} {1}' -f $FilePath, ($Arguments -join ' ')
        $logText = @(
            'Command: ' + $commandLine
            'WorkingDirectory: ' + $WorkingDirectory
            'ExitCode: ' + $exitCode
            ''
            $lines
        ) -join [Environment]::NewLine
        Write-Log -Path $logPath -Text $logText

        if ($exitCode -eq 0) {
            $status = 'passed'
            $reason = $null
        } else {
            $reason = 'Command exited with a non-zero code; inspect the step log.'
        }
    } catch {
        $exitCode = 1
        $reason = 'Command could not be started or its output could not be recorded.'
        $commandLine = '{0} {1}' -f $FilePath, ($Arguments -join ' ')
        Write-Log -Path $logPath -Text ((@(
            'Command: ' + $commandLine
            'WorkingDirectory: ' + $WorkingDirectory
            'ExitCode: 1'
            ''
            $reason
            'StartupError:'
            (Protect-Text $_.Exception.ToString())
        )) -join [Environment]::NewLine)
    }

    $record = Add-Result -Name $Name -Status $status -ExitCode $exitCode -LogPath $logPath -Reason $reason -Details $null
    $record.durationSeconds = [Math]::Round(([DateTime]::UtcNow - $started).TotalSeconds, 3)
    return $record
}

function Assert-Passed {
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary]$Result)

    if ($Result.status -ne 'passed') {
        throw 'A verification step failed.'
    }
}

function Mark-PendingStepsSkipped {
    param([Parameter(Mandatory = $true)][string]$Reason)

    $completed = @{}
    foreach ($result in $script:Results) { $completed[$result.name] = $true }
    foreach ($name in $script:PlanNames) {
        if (-not $completed.ContainsKey($name)) {
            [void](Add-SkippedResult -Name $name -Reason $Reason)
        }
    }
}

function Update-DotnetTestSummary {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Result,
        [Parameter(Mandatory = $true)][string]$TrxPath,
        [Parameter(Mandatory = $true)][bool]$DatabaseConfigured,
        [Parameter(Mandatory = $true)][bool]$PerformanceBenchmarkEnabled
    )

    if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) {
        $script:TestSummary = [ordered]@{ summary = 'TRX test result was not produced.'; trxPath = $TrxPath }
        $Result.details = $script:TestSummary
        $Result.status = 'failed'
        $Result.exitCode = 1
        $Result.reason = 'dotnet test did not produce the expected TRX result.'
        return
    }

    try {
        $document = [xml](Get-Content -LiteralPath $TrxPath -Raw)
        $counters = $document.SelectSingleNode("//*[local-name()='Counters']")
        $testResults = @($document.SelectNodes("//*[local-name()='UnitTestResult']"))
        if ($null -eq $counters) {
            throw 'TRX Counters element is missing.'
        }

        $skippedResults = @($testResults | Where-Object {
            $_.GetAttribute('outcome') -in @('Skipped', 'NotExecuted')
        })
        $optionalSkipped = @($skippedResults | Where-Object {
            ([string]$_.GetAttribute('testName')) -match 'PerformanceBenchmarks\.HotReadEndpointsOverLargeHistory'
        })
        $databaseSkipped = @($skippedResults | Where-Object {
            ([string]$_.GetAttribute('testName')) -notmatch 'PerformanceBenchmarks\.HotReadEndpointsOverLargeHistory'
        })
        $unexpectedSkipped = @()
        if ($DatabaseConfigured) {
            $unexpectedSkipped = @($databaseSkipped)
        }
        if ($PerformanceBenchmarkEnabled) {
            $unexpectedSkipped = @($unexpectedSkipped + $optionalSkipped)
        }

        $script:TestSummary = [ordered]@{
            passed = [int]$counters.GetAttribute('passed')
            failed = [int]$counters.GetAttribute('failed')
            skipped = $skippedResults.Count
            total = [int]$counters.GetAttribute('total')
            optionalSkipped = $optionalSkipped.Count
            databaseSkippedWithoutExplicitUrl = if ($DatabaseConfigured) { 0 } else { $databaseSkipped.Count }
            unexpectedSkipped = $unexpectedSkipped.Count
            trxPath = $TrxPath
        }
        $Result.details = $script:TestSummary
        if ($unexpectedSkipped.Count -gt 0) {
            $Result.status = 'failed'
            $Result.exitCode = 1
            $Result.reason = 'dotnet test skipped tests outside the explicitly optional performance benchmark.'
        }
    } catch {
        $script:TestSummary = [ordered]@{
            summary = 'TRX test result could not be parsed.'
            trxPath = $TrxPath
        }
        $Result.details = $script:TestSummary
        $Result.status = 'failed'
        $Result.exitCode = 1
        $Result.reason = 'dotnet test result parsing failed; inspect the TRX file and step log.'
    }
}

function Get-EnvironmentSnapshot {
    $snapshot = @{}
    $namesChanged = New-Object 'System.Collections.Generic.List[string]'
    $explicitNames = @(
        'YF_CONFIG_PATH',
        'YF_BOOTSTRAP_PASSWORD',
        'YF_EF_DESIGN_CONNECTION',
        'YF_TEST_RESULTS_PATH',
        'YF_TEST_API_DIR',
        'YF_UPDATE_OPENAPI',
        'ASPNETCORE_ENVIRONMENT',
        'DOTNET_ENVIRONMENT'
    )
    foreach ($entry in Get-ChildItem Env:) {
        if ($entry.Name -like 'App__*' -or $entry.Name -like 'App:*' -or $explicitNames -contains $entry.Name) {
            $snapshot[$entry.Name] = [string]$entry.Value
            [void]$namesChanged.Add($entry.Name)
        }
    }
    # Track explicit variables even when they were absent so values assigned by
    # this script (notably the design-time EF connection) are removed on exit.
    foreach ($name in $explicitNames) {
        if (-not $snapshot.ContainsKey($name)) {
            $snapshot[$name] = $null
            [void]$namesChanged.Add($name)
        }
    }
    return [ordered]@{ values = $snapshot; names = $namesChanged }
}

function Clear-ConfigurationEnvironment {
    param([Parameter(Mandatory = $true)][hashtable]$Snapshot)

    foreach ($name in $Snapshot.names) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
}

function Restore-ConfigurationEnvironment {
    param([Parameter(Mandatory = $true)][hashtable]$Snapshot)

    foreach ($name in $Snapshot.names) {
        [Environment]::SetEnvironmentVariable($name, $Snapshot.values[$name], 'Process')
    }
}

$testDatabaseUrl = [Environment]::GetEnvironmentVariable('YF_TEST_DATABASE_URL', 'Process')
Add-RedactionValue $testDatabaseUrl
$originalDesignConnection = [Environment]::GetEnvironmentVariable('YF_EF_DESIGN_CONNECTION', 'Process')
Add-RedactionValue $originalDesignConnection
$openApiUpdateValue = [Environment]::GetEnvironmentVariable('YF_UPDATE_OPENAPI', 'Process')

# Protect the URL's user-info separately in case a child tool echoes only the
# credentials portion rather than the complete value.
if (-not [string]::IsNullOrWhiteSpace($testDatabaseUrl)) {
    try {
        $testUriForRedaction = [Uri]$testDatabaseUrl
        Add-RedactionValue $testUriForRedaction.UserInfo
    } catch { }
}

$environmentSnapshot = Get-EnvironmentSnapshot
$safeEfDesignConnection = 'Server=127.0.0.1;Port=3306;Database=yf_ef_model_check;User ID=unused;Password=unused;SslMode=None;Connection Timeout=1'
Add-RedactionValue $safeEfDesignConnection

try {
    Clear-ConfigurationEnvironment -Snapshot $environmentSnapshot

    if ($openApiUpdateValue -match '^(?i:1|true|yes)$') {
        [void](Add-Result -Name 'openapi-update-guard' -Status 'failed' -ExitCode 1 -LogPath $null -Reason 'YF_UPDATE_OPENAPI must not enable snapshot generation during verification.' -Details @{ updateDisabled = $false })
        throw 'A verification step failed.'
    }
    [void](Add-Result -Name 'openapi-update-guard' -Status 'passed' -ExitCode 0 -LogPath $null -Reason $null -Details @{ updateDisabled = $true })

    if ([string]::IsNullOrWhiteSpace($testDatabaseUrl)) {
        $script:DatabaseMode = 'skipped-no-explicit-local-url'
        [void](Add-SkippedResult -Name 'database-configuration' -Reason 'YF_TEST_DATABASE_URL is not set; database-backed .NET and HTTP checks are skipped.')
    } else {
        $testDatabaseUri = $null
        $validUri = [Uri]::TryCreate($testDatabaseUrl, [UriKind]::Absolute, [ref]$testDatabaseUri)
        $localHost = $false
        if ($validUri) {
            $localHost = $testDatabaseUri.Host -in @('127.0.0.1', 'localhost', '::1')
        }
        if (-not $validUri -or $testDatabaseUri.Scheme -ine 'mysql' -or -not $localHost) {
            $script:DatabaseMode = 'invalid-url'
            $invalid = Add-Result -Name 'database-configuration' -Status 'failed' -ExitCode 1 -LogPath $null -Reason 'YF_TEST_DATABASE_URL must be an explicit local mysql:// URL.' -Details @{ configured = $false }
            throw 'A verification step failed.'
        }
        $script:DatabaseMode = 'enabled-local-url-redacted'
        [void](Add-Result -Name 'database-configuration' -Status 'passed' -ExitCode 0 -LogPath $null -Reason $null -Details @{ configured = $true; host = 'loopback'; scheme = 'mysql' })
    }

    $sdkResult = Invoke-VerificationStep -Name 'dotnet-sdk' -FilePath 'dotnet' -Arguments @('--version') -WorkingDirectory $serverRoot
    Assert-Passed $sdkResult
    $sdkOutput = Get-Content -LiteralPath $sdkResult.logPath -Raw
    $sdkMatch = [Regex]::Match($sdkOutput, '(?m)^\s*(\d+\.\d+\.\d+)\s*$')
    if (-not $sdkMatch.Success) {
        $sdkMatch = [Regex]::Match($sdkOutput, '(\d+\.\d+\.\d+)')
    }
    if (-not $sdkMatch.Success -or -not $sdkMatch.Groups[1].Value.StartsWith('8.')) {
        $sdkResult.status = 'failed'
        $sdkResult.exitCode = 1
        $sdkResult.reason = 'The active SDK is not a .NET 8 SDK selected by server_dotnet/global.json.'
        throw 'A verification step failed.'
    }
    $script:DotnetSdkVersion = $sdkMatch.Groups[1].Value
    $sdkResult.details = @{ version = $script:DotnetSdkVersion; globalJson = (Join-Path $serverRoot 'global.json') }

    $apiProject = '.\Yf.Api\Yf.Api.csproj'
    $testHostProject = '.\TestHost\Yf.Api.TestHost.csproj'
    $testsProject = '.\tests\Yf.Api.Tests.csproj'
    $restoreArguments = @('--locked-mode', '--verbosity', 'minimal')
    $buildArguments = @('--configuration', 'Debug', '--no-restore', '--verbosity', 'minimal')

    $step = Invoke-VerificationStep -Name 'restore-api' -FilePath 'dotnet' -Arguments (@('restore', $apiProject) + $restoreArguments) -WorkingDirectory $serverRoot
    Assert-Passed $step
    $step = Invoke-VerificationStep -Name 'restore-testhost' -FilePath 'dotnet' -Arguments (@('restore', $testHostProject) + $restoreArguments) -WorkingDirectory $serverRoot
    Assert-Passed $step
    $step = Invoke-VerificationStep -Name 'restore-tests' -FilePath 'dotnet' -Arguments (@('restore', $testsProject) + $restoreArguments) -WorkingDirectory $serverRoot
    Assert-Passed $step

    $step = Invoke-VerificationStep -Name 'build-api' -FilePath 'dotnet' -Arguments (@('build', $apiProject) + $buildArguments) -WorkingDirectory $serverRoot
    Assert-Passed $step
    $step = Invoke-VerificationStep -Name 'build-testhost' -FilePath 'dotnet' -Arguments (@('build', $testHostProject) + $buildArguments) -WorkingDirectory $serverRoot
    Assert-Passed $step
    $step = Invoke-VerificationStep -Name 'build-tests' -FilePath 'dotnet' -Arguments (@('build', $testsProject) + $buildArguments) -WorkingDirectory $serverRoot
    Assert-Passed $step

    $step = Invoke-VerificationStep -Name 'dotnet-test' -FilePath 'dotnet' -Arguments @('test', $testsProject, '--configuration', 'Debug', '--no-build', '--no-restore', '--results-directory', $runRoot, '--logger', 'console;verbosity=normal', '--logger', 'trx;LogFileName=dotnet-test.trx') -WorkingDirectory $serverRoot
    Update-DotnetTestSummary -Result $step -TrxPath $script:TestResultsPath -DatabaseConfigured (-not [string]::IsNullOrWhiteSpace($testDatabaseUrl)) -PerformanceBenchmarkEnabled ([Environment]::GetEnvironmentVariable('YF_PERF_BENCHMARK', 'Process') -eq '1')
    Assert-Passed $step

    # The design-time factory is deliberately pointed at a disposable-looking
    # loopback name.  has-pending-model-changes compares the compiled model and
    # snapshot; it must never apply a migration or use a project connection.
    [Environment]::SetEnvironmentVariable('YF_EF_DESIGN_CONNECTION', $safeEfDesignConnection, 'Process')
    $step = Invoke-VerificationStep -Name 'ef-model-check' -FilePath 'dotnet' -Arguments @('ef', 'migrations', 'has-pending-model-changes', '--project', $apiProject, '--startup-project', $apiProject, '--configuration', 'Debug', '--no-build') -WorkingDirectory $serverRoot
    Assert-Passed $step

    if ($SkipHttp) {
        [void](Add-SkippedResult -Name 'http-isolated' -Reason 'HTTP contract validation was disabled with -SkipHttp.')
    } elseif ([string]::IsNullOrWhiteSpace($testDatabaseUrl)) {
        [void](Add-SkippedResult -Name 'http-isolated' -Reason 'YF_TEST_DATABASE_URL is not set; HTTP contract validation requires an explicit local test database.')
    } else {
        $previousResultsPath = [Environment]::GetEnvironmentVariable('YF_TEST_RESULTS_PATH', 'Process')
        [Environment]::SetEnvironmentVariable('YF_TEST_RESULTS_PATH', (Join-Path $runRoot 'http-results.json'), 'Process')
        try {
            $step = Invoke-VerificationStep -Name 'http-isolated' -FilePath 'python' -Arguments @((Join-Path $serverRoot 'scripts\test-isolated.py')) -WorkingDirectory $repositoryRoot
        } finally {
            [Environment]::SetEnvironmentVariable('YF_TEST_RESULTS_PATH', $previousResultsPath, 'Process')
        }
        Assert-Passed $step
    }

    if (-not $IncludeMaintenance) {
        [void](Add-SkippedResult -Name 'maintenance' -Reason 'Real MySQL maintenance validation is optional; use -IncludeMaintenance to run it.')
    } elseif ([string]::IsNullOrWhiteSpace($testDatabaseUrl)) {
        [void](Add-SkippedResult -Name 'maintenance' -Reason 'YF_TEST_DATABASE_URL is not set; maintenance validation requires an explicit local test database.')
    } else {
        $step = Invoke-VerificationStep -Name 'maintenance' -FilePath 'python' -Arguments @((Join-Path $serverRoot 'scripts\test-maintenance.py')) -WorkingDirectory $repositoryRoot
        Assert-Passed $step
        $maintenanceReport = Join-Path $repositoryRoot '.artifacts\tests\maintenance-results.json'
        if (Test-Path -LiteralPath $maintenanceReport -PathType Leaf) {
            Copy-Item -LiteralPath $maintenanceReport -Destination (Join-Path $runRoot 'maintenance-results.json') -Force
        }
    }

    $npmCommand = 'npm.cmd'
    $step = Invoke-VerificationStep -Name 'npm-ci' -FilePath $npmCommand -Arguments @('ci') -WorkingDirectory $webRoot
    Assert-Passed $step
    $step = Invoke-VerificationStep -Name 'npm-lint' -FilePath $npmCommand -Arguments @('run', 'lint') -WorkingDirectory $webRoot
    Assert-Passed $step
    $step = Invoke-VerificationStep -Name 'npm-test' -FilePath $npmCommand -Arguments @('test') -WorkingDirectory $webRoot
    Assert-Passed $step
    $step = Invoke-VerificationStep -Name 'npm-build' -FilePath $npmCommand -Arguments @('run', 'build') -WorkingDirectory $webRoot
    Assert-Passed $step
} catch {
    $script:ExitCode = 1
    $script:FailureMessage = 'A verification step failed; inspect the failed step log in the run directory.'
    $runtimeFailure = @($script:Results | Where-Object { $_.status -eq 'failed' })
    if ($runtimeFailure.Count -eq 0) {
        $runtimeLog = Join-Path $runRoot 'verification-runtime.log'
        Write-Log -Path $runtimeLog -Text ((@(
            'The verification runner stopped outside an external command.'
            'StartupOrRunnerError:'
            (Protect-Text $_.Exception.ToString())
        )) -join [Environment]::NewLine)
        [void](Add-Result -Name 'verification-runtime' -Status 'failed' -ExitCode 1 -LogPath $runtimeLog -Reason $script:FailureMessage -Details $null)
    }
    Mark-PendingStepsSkipped -Reason 'Not run because an earlier verification step failed.'
} finally {
    Restore-ConfigurationEnvironment -Snapshot $environmentSnapshot

    $failed = @($script:Results | Where-Object { $_.status -eq 'failed' })
    $skipped = @($script:Results | Where-Object { $_.status -eq 'skipped' })
    $testSkipped = 0
    if ($null -ne $script:TestSummary -and $script:TestSummary.Contains('skipped')) {
        $testSkipped = [int]$script:TestSummary.skipped
    }
    $fullCoverage = ($failed.Count -eq 0 -and $skipped.Count -eq 0 -and $testSkipped -eq 0)
    $overall = if ($script:ExitCode -ne 0 -or $failed.Count -gt 0) { 'failed' } elseif ($skipped.Count -gt 0 -or $testSkipped -gt 0) { 'passed-with-skips' } else { 'passed' }
    $report = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [DateTime]::UtcNow.ToString('o')
        repositoryRoot = $repositoryRoot
        runDirectory = $runRoot
        status = $overall
        fullCoverage = $fullCoverage
        databaseChecks = $script:DatabaseMode
        browserAutomation = 'not-run'
        dotnetSdk = $script:DotnetSdkVersion
        testSummary = $script:TestSummary
        steps = $script:Results.ToArray()
    }
    try {
        $json = $report | ConvertTo-Json -Depth 8
        Write-Log -Path $reportPath -Text ($json + [Environment]::NewLine)
    } catch {
        $script:ExitCode = 1
    }
}

if ($script:ExitCode -ne 0) {
    Write-Host ('verify-all FAILED. Report: ' + $reportPath)
    exit 1
}

Write-Host ('verify-all {0}. fullCoverage={1}. Report: {2}' -f $overall, $fullCoverage, $reportPath)
exit 0
