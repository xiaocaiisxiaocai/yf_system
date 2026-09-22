#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot '..\deploy\maintenance-common.ps1')

function Reject([scriptblock]$Operation,[string]$Name) {
    $rejected=$false
    try { & $Operation | Out-Null } catch { $rejected=$true }
    if (!$rejected) { throw "Expected rejection: $Name" }
    Write-Output "PASS $Name"
}

$tempBase=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\.artifacts\tests\tmp')).TrimEnd('\')+'\'
New-Item -ItemType Directory -Path $tempBase -Force | Out-Null
$root=[IO.Path]::GetFullPath((Join-Path $tempBase ('yf-deploy-security-'+[guid]::NewGuid().ToString('N'))))
if (!$root.StartsWith($tempBase,[StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($root) -notmatch '^yf-deploy-security-[a-f0-9]{32}$') {
    throw 'Unsafe security fixture root.'
}
New-Item -ItemType Directory -Path $root | Out-Null
Protect-YfDirectory $root

try {
    $storage=Join-Path $root 'storage'
    New-Item -ItemType Directory -Path $storage | Out-Null
    Reject { Assert-YfSeparate @($storage,(Join-Path $storage 'appsettings.json')) } 'configuration inside writable storage refused'
    Reject { Assert-YfSeparate @((Join-Path $root 'site'),(Join-Path $root 'site\appsettings.json')) } 'configuration inside application refused'
    Reject { Assert-YfSeparate @((Join-Path $root 'package'),(Join-Path $root 'package\site')) } 'application inside package refused'

    $installScript=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\deploy\install-iis.ps1') -Raw -Encoding UTF8
    if ($installScript -notmatch [regex]::Escape('foreach ($other in @($PackageRoot,$SiteRoot,$ConfigPath))') -or
        $installScript -notmatch [regex]::Escape('Assert-YfSeparate @($PackageRoot,$SiteRoot,$ConfigPath,$storage)') -or
        $installScript -notmatch [regex]::Escape('(Within $storage $other) -or (Within $other $storage)') -or
        $installScript -notmatch [regex]::Escape('$maintenanceConfig = Read-YfMaintenanceConfig $ConfigPath') -or
        $installScript -notmatch [regex]::Escape('Protect-YfConfigurationFile $ConfigPath $identity')) {
        throw 'Installer does not apply the tested path and configuration ACL guards.'
    }
    $manifestVerification=$installScript.IndexOf('Get-FileHash -LiteralPath $file -Algorithm SHA256',[StringComparison]::Ordinal)
    $sharedImportNeedle='. (Join-Path $PackageRoot ''maintenance-common.ps1'')'
    $sharedCodeImport=$installScript.IndexOf($sharedImportNeedle,[StringComparison]::Ordinal)
    if ($manifestVerification -lt 0 -or $sharedCodeImport -le $manifestVerification) {
        throw 'Installer imports shared deployment code before package hash verification.'
    }
    $publishedConfigGuard=$installScript.IndexOf('Assert-YfPublishedConfig $PackageRoot',[StringComparison]::Ordinal)
    $effectiveConfigGuard=$installScript.IndexOf('Assert-YfInstallationConfiguration $SiteName',[StringComparison]::Ordinal)
    $firstDeploymentWrite=$installScript.IndexOf('New-Item -ItemType Directory -Path $SiteRoot',[StringComparison]::Ordinal)
    if ($publishedConfigGuard -lt 0 -or $effectiveConfigGuard -lt 0 -or $firstDeploymentWrite -lt 0 -or
        $publishedConfigGuard -ge $firstDeploymentWrite -or $effectiveConfigGuard -ge $firstDeploymentWrite) {
        throw 'Installer does not validate published and inherited configuration before deployment writes.'
    }
    $poolCreated=$installScript.IndexOf('New-WebAppPool -Name $AppPoolName',[StringComparison]::Ordinal)
    $poolIdentitySet=$installScript.IndexOf("-Name processModel.identityType -Value 'ApplicationPoolIdentity'",[StringComparison]::Ordinal)
    $poolProfileDisabled=$installScript.IndexOf('-Name processModel.loadUserProfile -Value $false',[StringComparison]::Ordinal)
    $siteCreated=$installScript.IndexOf('New-Website -Name $SiteName',[StringComparison]::Ordinal)
    if ($poolCreated -lt 0 -or $poolIdentitySet -le $poolCreated -or $poolProfileDisabled -le $poolCreated -or $siteCreated -lt 0 -or
        $poolIdentitySet -ge $siteCreated -or $poolProfileDisabled -ge $siteCreated) {
        throw 'Installer does not fix the dedicated pool identity and profile before creating the site.'
    }
    Write-Output 'PASS installer applies path, configuration, ACL and pre-write environment guards'

    $externalConfig='D:\YfConfig\appsettings.Production.json'
    $effectiveVariables=@(
        [pscustomobject]@{Name='YF_CONFIG_PATH';Value=$externalConfig},
        [pscustomobject]@{Name='ASPNETCORE_ENVIRONMENT';Value='Production'}
    )
    $convertedEmpty=ConvertTo-YfConfigurationVariables @()
    $convertedNull=ConvertTo-YfConfigurationVariables $null
    if (@($convertedEmpty).Count -ne 0 -or @($convertedNull).Count -ne 0) {
        throw 'Empty IIS environment collections were not normalized.'
    }
    Assert-YfConfigurationEnvironment `
        -AspNetCoreVariables $convertedEmpty `
        -ApplicationPoolVariableNames @() `
        -DefaultPoolVariableNames @() `
        -MachineVariableNames @() `
        -ProcessVariableNames @()
    Assert-YfConfigurationEnvironment `
        -AspNetCoreVariables $convertedNull `
        -ApplicationPoolVariableNames @() `
        -DefaultPoolVariableNames @() `
        -MachineVariableNames @() `
        -ProcessVariableNames @()
    Write-Output 'PASS empty and null IIS environment collections remain empty through the shared helper chain'
    Assert-YfConfigurationEnvironment $effectiveVariables @() @() @() @() $externalConfig
    Reject {
        Assert-YfConfigurationEnvironment @([pscustomobject]@{Name='App__ConnectionString';Value='wrong-database'}) @() @() @() @()
    } 'inherited web.config App override refused'
    Reject {
        Assert-YfConfigurationEnvironment @() @('App:ConnectionString') @() @() @()
    } 'application pool App override refused'
    Reject {
        Assert-YfConfigurationEnvironment @() @() @('App__ConnectionString') @() @()
    } 'application pool default App override refused'
    Reject {
        Assert-YfConfigurationEnvironment @() @() @() @('App__ConnectionString') @()
    } 'machine App override refused'
    Reject {
        Assert-YfConfigurationEnvironment @() @() @() @() @('App__ConnectionString')
    } 'deployment process App override refused'
    Reject {
        Assert-YfConfigurationEnvironment @([pscustomobject]@{Name='YF_CONFIG_PATH';Value=$externalConfig}) @() @() @() @()
    } 'inherited YF_CONFIG_PATH refused during installation'
    Reject {
        Assert-YfConfigurationEnvironment $effectiveVariables @() @() @() @() 'D:\YfConfig\different.json'
    } 'mismatched effective YF_CONFIG_PATH refused'
    Assert-YfApplicationPoolProcessModel 'ApplicationPoolIdentity' $false
    Reject {
        Assert-YfApplicationPoolProcessModel 'SpecificUser' $false
    } 'custom application pool identity refused by maintenance guard'
    Reject {
        Assert-YfApplicationPoolProcessModel 'ApplicationPoolIdentity' $true
    } 'application pool user profile refused by maintenance guard'
    Write-Output 'PASS shared environment guard covers every IIS installation configuration source'

    $aclFixture=Join-Path $root 'acl-fixture.json'
    [IO.File]::WriteAllText($aclFixture,'{}',(New-Object Text.UTF8Encoding($false)))
    $usersSid=New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'
    $before=Get-Acl -LiteralPath $aclFixture
    $before.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($usersSid,'FullControl','Allow')))
    Set-Acl -LiteralPath $aclFixture -AclObject $before

    $readSid=New-Object Security.Principal.SecurityIdentifier 'S-1-5-20'
    Protect-YfConfigurationFile $aclFixture $readSid
    $acl=Get-Acl -LiteralPath $aclFixture
    $rules=@($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
    $readRules=@($rules | Where-Object { $_.IdentityReference -eq $readSid })
    $broadRules=@($rules | Where-Object { $_.IdentityReference -eq $usersSid })
    $writeMask=[Security.AccessControl.FileSystemRights]::WriteData -bor [Security.AccessControl.FileSystemRights]::AppendData -bor [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor [Security.AccessControl.FileSystemRights]::WriteAttributes -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    if (!$acl.AreAccessRulesProtected -or $broadRules.Count -ne 0 -or $readRules.Count -ne 1 -or
        ($readRules[0].FileSystemRights -band $writeMask) -ne 0 -or
        ($readRules[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::Read) -ne [Security.AccessControl.FileSystemRights]::Read) {
        throw 'Protected configuration ACL did not remove broad access or leave the application identity read-only.'
    }
    Write-Output 'PASS protected configuration ACL removes broad access and leaves application identity read-only'

    function Write-TestConfig([string]$Name,[string]$Server,[string]$SslMode,[string]$CaFile='',[switch]$ClientCertificate) {
        $connectionString="Server=$Server;Port=3306;Database=yf_security_test;User ID=test_user;Password=fixture-only;SSL Mode=$SslMode"
        if ($CaFile) { $connectionString+=';CACertificateFile='+$CaFile }
        if ($ClientCertificate) { $connectionString+=';CertificateFile=C:\fixture-client.pfx' }
        $value=[ordered]@{
            App=[ordered]@{
                ConnectionString=$connectionString
                StorageRoot=$storage
                WebBaseUrl='https://fixture.example.test'
                CookieSecure=$true
            }
        }
        $path=Join-Path $root $Name
        Write-YfJson $path $value
        return $path
    }

    foreach ($loopback in @('localhost','127.0.0.1','::1')) {
        $local=Read-YfMaintenanceConfig (Write-TestConfig ('local-'+($loopback -replace ':','_')+'.json') $loopback 'None')
        if ($local.SslMode -ne 'DISABLED') { throw 'Local database compatibility mode was not preserved.' }
    }
    Write-Output 'PASS loopback database compatibility modes remain available'

    foreach ($mode in @('None','Disabled','Preferred','Required','VerifyCA')) {
        $path=Write-TestConfig ('remote-'+$mode+'.json') 'db.example.test' $mode
        Reject { Read-YfMaintenanceConfig $path } "remote database $mode refused"
    }

    $storageCa=Join-Path $storage 'database-ca.pem'
    [IO.File]::WriteAllText($storageCa,'fixture-ca-placeholder',(New-Object Text.UTF8Encoding($false)))
    Reject {
        Read-YfMaintenanceConfig (Write-TestConfig 'remote-storage-ca.json' 'db.example.test' 'VerifyFull' $storageCa)
    } 'database CA inside writable storage refused'
    $caFixture=Join-Path $root 'database-ca.pem'
    [IO.File]::WriteAllText($caFixture,'fixture-ca-placeholder',(New-Object Text.UTF8Encoding($false)))
    $remote=Read-YfMaintenanceConfig (Write-TestConfig 'remote-verify-full.json' 'db.example.test' 'VerifyFull' $caFixture)
    if ($remote.SslMode -ne 'VERIFY_IDENTITY' -or $remote.CaFile -ne $caFixture) {
        throw 'Remote VerifyFull or CA path was not preserved.'
    }
    Reject {
        Read-YfMaintenanceConfig (Write-TestConfig 'remote-missing-ca.json' 'db.example.test' 'VerifyFull' (Join-Path $root 'missing-ca.pem'))
    } 'missing database CA file refused'
    Reject {
        Read-YfMaintenanceConfig (Write-TestConfig 'remote-client-certificate.json' 'db.example.test' 'VerifyFull' $caFixture -ClientCertificate)
    } 'unsupported client certificate settings refused'

    $defaultsDirectory=Join-Path $root 'defaults'
    New-Item -ItemType Directory -Path $defaultsDirectory | Out-Null
    Protect-YfDirectory $defaultsDirectory
    $defaultsPath=Write-YfMySqlDefaults $remote $defaultsDirectory
    $defaultsText=Get-Content -LiteralPath $defaultsPath -Raw -Encoding UTF8
    if ($defaultsText -notmatch '(?m)^ssl-mode=VERIFY_IDENTITY\r?$' -or
        $defaultsText -notmatch '(?m)^ssl-ca=') {
        throw 'MySQL defaults did not preserve verified TLS and CA settings.'
    }
    Write-Output 'PASS remote database requires VerifyFull and forwards the CA file'
} finally {
    if (Test-Path -LiteralPath $root) {
        $resolved=[IO.Path]::GetFullPath($root)
        if (!$resolved.StartsWith($tempBase,[StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolved) -notmatch '^yf-deploy-security-[a-f0-9]{32}$') {
            throw 'Refusing unsafe security fixture cleanup.'
        }
        Assert-YfNoLinks $resolved
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    if (Test-Path -LiteralPath $root) { throw 'Security fixture cleanup failed.' }
    Write-Output "PASS isolated security fixture removed: $root"
}
