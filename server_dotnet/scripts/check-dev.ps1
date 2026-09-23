#Requires -Version 5.1
[CmdletBinding()]
param([string]$ConfigPath)

$ErrorActionPreference = 'Stop'
$apiDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Yf.Api'))
$previousConfig = [Environment]::GetEnvironmentVariable('YF_CONFIG_PATH', 'Process')
$previousEnvironment = [Environment]::GetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Process')
$resultCode = 1

try {
    if ([string]::IsNullOrWhiteSpace($ConfigPath)) { $ConfigPath = Join-Path $apiDirectory 'appsettings.Local.json' }
    $ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
    if (!(Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        throw 'Local configuration file is missing.'
    }
    Push-Location $apiDirectory
    try {
        # Always rebuild: an existing DLL is not evidence that current source compiles.
        $buildOutput = & dotnet build .\Yf.Api.csproj --configuration Debug --no-restore --verbosity quiet 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw 'Build failed. Run dotnet build server_dotnet/Yf.Api/Yf.Api.csproj for compiler diagnostics.'
        }
        $env:YF_CONFIG_PATH = $ConfigPath
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        # Preserve App environment overrides, exactly as normal startup does.
        # The backend reuses its actual configuration, storage and scanner contracts.
        & dotnet .\bin\Debug\net8.0\Yf.Api.dll --check-development-readiness
        $resultCode = $LASTEXITCODE
    } finally { Pop-Location }
} catch {
    # Never print arbitrary exception messages, connection strings or private JSON.
    [ordered]@{
        readyForStartup = $false
        checks = @{ configurationOrBuild = $false }
        issues = @('Development check could not run. Verify the configuration path and run dotnet build server_dotnet/Yf.Api/Yf.Api.csproj for diagnostics.')
    } | ConvertTo-Json -Depth 5
} finally {
    [Environment]::SetEnvironmentVariable('YF_CONFIG_PATH', $previousConfig, 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', $previousEnvironment, 'Process')
}
exit $resultCode
