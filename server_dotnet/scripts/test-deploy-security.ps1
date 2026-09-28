#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot '..\deploy\maintenance-common.ps1')

function Reject([scriptblock]$Operation,[string]$Name,[string]$ExpectedMessage='') {
    # A rejection only counts when it fails for the expected reason, not for an unrelated error.
    $message=$null
    try { & $Operation | Out-Null } catch { $message=$_.Exception.Message }
    if ($null -eq $message) { throw "Expected rejection: $Name" }
    if ($ExpectedMessage -and $message.IndexOf($ExpectedMessage,[StringComparison]::Ordinal) -lt 0) {
        throw "Rejection for '$Name' had an unexpected message: $message"
    }
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
    $warningClient=Join-Path $root 'native-warning.cmd'
    [IO.File]::WriteAllText($warningClient,"@echo off`r`necho 0`r`necho fixture warning 1>&2`r`nexit /b 0`r`n",(New-Object Text.UTF8Encoding($false)))
    $warningLog=Join-Path $root 'native-warning.stderr.log'
    $captured=Invoke-YfNativeCapture $warningClient @() $warningLog
    if ($captured.ExitCode -ne 0 -or @($captured.Output).Count -ne 1 -or $captured.Output[0].Trim() -ne '0' -or
        !(Test-Path -LiteralPath $warningLog -PathType Leaf) -or
        (Get-Content -LiteralPath $warningLog -Raw) -notmatch 'fixture warning') {
        throw 'Native stderr warning was not captured without interrupting Windows PowerShell 5.1.'
    }
    Write-Output 'PASS native stderr warning with exit zero remains nonfatal'
    $failureClient=Join-Path $root 'native-failure.cmd'
    [IO.File]::WriteAllText($failureClient,"@echo off`r`necho fixture failure 1>&2`r`nexit /b 7`r`n",(New-Object Text.UTF8Encoding($false)))
    $failed=Invoke-YfNativeCapture $failureClient @() (Join-Path $root 'native-failure.stderr.log')
    if ($failed.ExitCode -ne 7) { throw 'Native nonzero exit code was not preserved.' }
    Write-Output 'PASS native nonzero exit code remains authoritative'
    $overlap='Maintenance paths must not overlap.'
    Reject { Assert-YfSeparate @($storage,(Join-Path $storage 'appsettings.json')) } 'configuration inside writable storage refused' $overlap
    Reject { Assert-YfSeparate @((Join-Path $root 'site'),(Join-Path $root 'site\appsettings.json')) } 'configuration inside application refused' $overlap
    Reject { Assert-YfSeparate @((Join-Path $root 'package'),(Join-Path $root 'package\site')) } 'application inside package refused' $overlap

    $installScript=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\deploy\install-iis.ps1') -Raw -Encoding UTF8
    if ($installScript -notmatch [regex]::Escape('foreach ($other in @($PackageRoot,$SiteRoot,$ConfigPath))') -or
        $installScript -notmatch [regex]::Escape('Assert-YfSeparate @($PackageRoot,$SiteRoot,$ConfigPath,$storage,$LogRoot)') -or
        $installScript -notmatch [regex]::Escape('Set-YfWebConfigEnvironment (Join-Path $SiteRoot ''web.config'') $ConfigPath') -or
        $installScript -match [regex]::Escape('CreateElement(''environmentVariable'')') -or
        $installScript -notmatch [regex]::Escape('Set-YfApplicationDirectoryAcl $storage $identity') -or
        $installScript -notmatch [regex]::Escape('Wait-YfHealth $origin $HealthCheckWaitSeconds $HealthRequestTimeoutSeconds') -or
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
    $singleWorkerSet=$installScript.IndexOf('-Name processModel.maxProcesses -Value 1',[StringComparison]::Ordinal)
    $singleWorkerValidated=$installScript.IndexOf('Assert-YfApplicationPoolProcessModel ($installedPool.processModel.identityType.ToString())',[StringComparison]::Ordinal)
    $siteCreated=$installScript.IndexOf('New-Website -Name $SiteName',[StringComparison]::Ordinal)
    if ($poolCreated -lt 0 -or $poolIdentitySet -le $poolCreated -or $poolProfileDisabled -le $poolCreated -or
        $singleWorkerSet -le $poolCreated -or $singleWorkerValidated -le $singleWorkerSet -or $siteCreated -lt 0 -or
        $poolIdentitySet -ge $siteCreated -or $poolProfileDisabled -ge $siteCreated -or
        $singleWorkerSet -ge $siteCreated -or $singleWorkerValidated -ge $siteCreated) {
        throw 'Installer does not fix and verify the dedicated single-worker pool before creating the site.'
    }
    foreach ($rollbackNeedle in @('$createdPool = $true','$createdSite = $true','Remove-Website -Name $SiteName',
            'Remove-WebAppPool -Name $AppPoolName','Set-Acl -LiteralPath $storage -AclObject $storageAcl',
            'Set-Acl -LiteralPath $LogRoot -AclObject $logRootAcl',
            'Set-Acl -LiteralPath $ConfigPath -AclObject $configAcl','$copiedTargets += $destination')) {
        if ($installScript.IndexOf($rollbackNeedle,[StringComparison]::Ordinal) -lt 0) {
            throw "Installer rollback guard is missing: $rollbackNeedle"
        }
    }
    # The post-start health probe must run inside the rollback try block.
    $installTry=$installScript.IndexOf('try {',[StringComparison]::Ordinal)
    $installStart=$installScript.IndexOf('Start-Website -Name $SiteName',[StringComparison]::Ordinal)
    $installHealth=$installScript.IndexOf('Wait-YfHealth $origin',[StringComparison]::Ordinal)
    $installCatch=if ($installHealth -ge 0) { $installScript.IndexOf('} catch {',$installHealth,[StringComparison]::Ordinal) } else { -1 }
    if ($installTry -lt 0 -or $installStart -le $installTry -or $installHealth -le $installStart -or $installCatch -le $installHealth) {
        throw 'Installer does not roll back when the post-start /health check fails.'
    }
    if ($installScript.IndexOf('try {',[StringComparison]::Ordinal) -gt $firstDeploymentWrite -or
        $installScript.IndexOf('Unsupported package manifest schema version.',[StringComparison]::Ordinal) -lt 0 -or
        $installScript.IndexOf('Package length mismatch:',[StringComparison]::Ordinal) -lt 0) {
        throw 'Installer does not wrap writes in rollback or fully validate manifest schema and lengths.'
    }
    Write-Output 'PASS installer applies path, configuration, ACL and pre-write environment guards'

    $maintainScript=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\deploy\maintain-iis.ps1') -Raw -Encoding UTF8
    if ($installScript -notmatch [regex]::Escape('Set-YfExternalConfigurationFallback $SiteRoot') -or
        $maintainScript -notmatch [regex]::Escape('Set-YfExternalConfigurationFallback $Root')) {
        throw 'Formal IIS installation or maintenance does not clear bundled configuration fallback.'
    }
    foreach ($maintenanceNeedle in @('$originalPhysicalPath=[string]$site.physicalPath','$migrationAttempted=$true',
            'Assert-YfDeploymentReadiness $NewSiteRoot $targetConfig','$pathSwitched=$true',
            'Set-YfWebConfigEnvironment (Join-Path $Root ''web.config'') $ExternalConfig',
            'Wait-YfHealth $targetConfig.Origin $HealthCheckWaitSeconds $HealthRequestTimeoutSeconds',
            '-Name physicalPath -Value $originalPhysicalPath','[int]$HealthCheckWaitSeconds = 120')) {
        if ($maintainScript.IndexOf($maintenanceNeedle,[StringComparison]::Ordinal) -lt 0) {
            throw "Maintenance rollback/readiness guard is missing: $maintenanceNeedle"
        }
    }
    if ($maintainScript.IndexOf('$migrationAttempted=$true',[StringComparison]::Ordinal) -gt
        $maintainScript.IndexOf('Invoke-YfMigration $NewSiteRoot $targetConfig.Path',[StringComparison]::Ordinal)) {
        throw 'Maintenance marks migration risk only after starting the migration.'
    }
    $siteConfigured=$maintainScript.IndexOf('Set-YfSiteConfig $NewSiteRoot',[StringComparison]::Ordinal)
    $readinessCheck=$maintainScript.IndexOf('Assert-YfDeploymentReadiness $NewSiteRoot $targetConfig',[StringComparison]::Ordinal)
    $migrationStart=$maintainScript.IndexOf('$migrationAttempted=$true',[StringComparison]::Ordinal)
    if ($maintainScript.IndexOf('--check-development-readiness',[StringComparison]::Ordinal) -ge 0 -or
        $siteConfigured -lt 0 -or $readinessCheck -le $siteConfigured -or $migrationStart -le $readinessCheck) {
        throw 'Maintenance does not pre-check the new package and configuration before migrating.'
    }
    Write-Output 'PASS maintenance distinguishes reversible path switches from migration recovery'
    $publishScript=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'publish-iis.ps1') -Raw -Encoding UTF8
    $verifyReleaseScript=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'verify-release.py') -Raw -Encoding UTF8
    if ($publishScript -notmatch [regex]::Escape('Add-Member -NotePropertyName AllowInsecureCookies -NotePropertyValue $insecureCookiesOverride -Force') -or
        $publishScript -notmatch [regex]::Escape('insecureCookies = [bool]$insecureCookiesOverride') -or
        $publishScript -notmatch [regex]::Escape('if ($needsInsecureCookies -and !$AllowInsecurePrivateConfiguration)') -or
        $verifyReleaseScript -notmatch [regex]::Escape('Packaged AllowInsecureCookies must be set only for a non-loopback HTTP origin') -or
        $verifyReleaseScript -notmatch [regex]::Escape('External configuration template must not opt in to insecure cookies') -or
        $installScript -notmatch [regex]::Escape('Production HTTPS configuration must not set App.AllowInsecureCookies=true.') -or
        $maintainScript -notmatch [regex]::Escape('Maintenance requires App.AllowInsecureCookies to be absent or false.') -or
        $maintainScript -notmatch [regex]::Escape('Restore requires App.AllowInsecureCookies to be absent or false.')) {
        throw 'Insecure cookie opt-in is not limited to explicit private HTTP packages.'
    }
    $example=(Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\deploy\appsettings.example.json') -Raw -Encoding UTF8 | ConvertFrom-Json).App
    $expectedExample=[ordered]@{
        CookieSecure=$true; AllowInsecureCookies=$false; MaxActiveSessionsPerUser=20; UploadMaxActiveSessionsPerUser=20
        UploadMaxPendingBytesPerUser=21474836480; BlobVerifyBytesPerCycle=2147483648; BatchDownloadMinBytesPerMinute=65536
        BatchDownloadMaxDurationMinutes=120; MailPendingTtlDays=3
    }
    foreach ($key in $expectedExample.Keys) {
        if (!$example.PSObject.Properties[$key] -or $example.$key -ne $expectedExample[$key]) {
            throw "Deployment example App.$key does not match the backend default."
        }
    }
    Write-Output 'PASS insecure cookie opt-in is private-HTTP only and the example carries backend defaults'
    $externalConfigPath='D:\YfConfig\appsettings.Production.json'
    $externalSite=Join-Path $root 'external-site'
    New-Item -ItemType Directory -Path $externalSite | Out-Null
    $bundled='{"Logging":{"LogLevel":{"Default":"Warning"}},"Serilog":{"MinimumLevel":"Information"},"AllowedHosts":"*","App":{"ConnectionString":"fixture-db","JwtSecret":"fixture-jwt","StorageRoot":"D:\\fixture","LogDirectory":"D:\\fixture-logs","AutoInitializeDatabase":true,"BootstrapPassword":"fixture-bootstrap","CookieSecure":true,"Smtp":{"Host":"smtp.fixture","Password":"fixture-smtp"}}}'
    [IO.File]::WriteAllText((Join-Path $externalSite 'appsettings.json'),$bundled,(New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $externalSite 'appsettings.example.json'),$bundled,(New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $externalSite 'appsettings.Production.json'),$bundled,(New-Object Text.UTF8Encoding($false)))
    Set-YfExternalConfigurationFallback $externalSite
    foreach ($name in @('appsettings.json','appsettings.Production.json','appsettings.example.json')) {
        $cleared=Get-Content -LiteralPath (Join-Path $externalSite $name) -Raw | ConvertFrom-Json
        $app=$cleared.App
        if ($app.ConnectionString -or $app.JwtSecret -or $app.StorageRoot -or $app.LogDirectory -or $app.AutoInitializeDatabase -or
            $app.BootstrapPassword -or $app.Smtp.Password) {
            throw 'Formal IIS site retained bundled credentials or storage configuration.'
        }
        if ($cleared.Logging.LogLevel.Default -ne 'Warning' -or $cleared.Serilog.MinimumLevel -ne 'Information' -or
            $cleared.AllowedHosts -ne '*' -or $app.CookieSecure -ne $true -or $app.Smtp.Host -ne 'smtp.fixture') {
            throw 'Clearing bundled credentials also discarded non-secret settings.'
        }
    }
    Write-Output 'PASS formal IIS install and maintenance clear bundled credentials and keep non-secret settings'

    # Regression: publish-iis.ps1 already writes ASPNETCORE_ENVIRONMENT; install and maintain must
    # replace it instead of appending a duplicate key (IIS rejects the whole site with 500.19).
    $webConfigRoot=Join-Path $root 'web-config-site'
    New-Item -ItemType Directory -Path $webConfigRoot | Out-Null
    $webConfig=Join-Path $webConfigRoot 'web.config'
    $publishedWebConfig=@'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <aspNetCore processPath="dotnet" arguments=".\Yf.Api.dll" hostingModel="inprocess">
        <environmentVariables>
          <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
          <environmentVariable name="yf_config_path" value="D:\Old\appsettings.json" />
          <environmentVariable name="DOTNET_GCServer" value="0" />
        </environmentVariables>
        <environmentVariables>
          <environmentVariable name="YF_CONFIG_PATH" value="D:\Older\appsettings.json" />
        </environmentVariables>
      </aspNetCore>
    </system.webServer>
  </location>
</configuration>
'@
    [IO.File]::WriteAllText($webConfig,$publishedWebConfig,(New-Object Text.UTF8Encoding($false)))
    foreach ($pass in 1..2) { Set-YfWebConfigEnvironment $webConfig $externalConfigPath }
    [xml]$written=Get-Content -LiteralPath $webConfig -Raw -Encoding UTF8
    $lists=@($written.SelectNodes('//aspNetCore/environmentVariables'))
    $writtenVariables=@($written.SelectNodes('//aspNetCore/environmentVariables/environmentVariable'))
    $duplicates=@($writtenVariables | ForEach-Object { $_.GetAttribute('name').ToUpperInvariant() } | Group-Object | Where-Object { $_.Count -gt 1 })
    $expectedVariables=@{ YF_CONFIG_PATH=$externalConfigPath; ASPNETCORE_ENVIRONMENT='Production'; DOTNET_ENVIRONMENT='Production'; DOTNET_GCSERVER='0' }
    if ($lists.Count -ne 1 -or $duplicates.Count -ne 0 -or $writtenVariables.Count -ne $expectedVariables.Count) {
        throw 'web.config environment variables were duplicated or lost.'
    }
    foreach ($variable in $writtenVariables) {
        if ([string]$variable.GetAttribute('value') -ne [string]$expectedVariables[$variable.GetAttribute('name').ToUpperInvariant()]) {
            throw "web.config environment variable $($variable.GetAttribute('name')) has the wrong value."
        }
    }
    Assert-YfPublishedConfig $webConfigRoot -AllowConfigPath
    Write-Output 'PASS web.config environment variables are replaced once, never duplicated'

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
    } 'inherited web.config App override refused' 'without App environment overrides'
    Reject {
        Assert-YfConfigurationEnvironment @() @('App:ConnectionString') @() @() @()
    } 'application pool App override refused' 'without App environment overrides'
    Reject {
        Assert-YfConfigurationEnvironment @() @() @('App__ConnectionString') @() @()
    } 'application pool default App override refused' 'without App environment overrides'
    Reject {
        Assert-YfConfigurationEnvironment @() @() @() @('App__ConnectionString') @()
    } 'machine App override refused' 'without App environment overrides'
    Reject {
        Assert-YfConfigurationEnvironment @() @() @() @() @('App__ConnectionString')
    } 'deployment process App override refused' 'without App environment overrides'
    Reject {
        Assert-YfConfigurationEnvironment @([pscustomobject]@{Name='YF_CONFIG_PATH';Value=$externalConfig}) @() @() @() @()
    } 'inherited YF_CONFIG_PATH refused during installation' 'without App environment overrides'
    Reject {
        Assert-YfConfigurationEnvironment $effectiveVariables @() @() @() @() 'D:\YfConfig\different.json'
    } 'mismatched effective YF_CONFIG_PATH refused' 'does not match the external configuration file'
    Assert-YfApplicationPoolProcessModel 'ApplicationPoolIdentity' $false 1
    Reject {
        Assert-YfApplicationPoolProcessModel 'SpecificUser' $false 1
    } 'custom application pool identity refused by maintenance guard' 'Deployment requires ApplicationPoolIdentity'
    Reject {
        Assert-YfApplicationPoolProcessModel 'ApplicationPoolIdentity' $true 1
    } 'application pool user profile refused by maintenance guard' 'Deployment requires ApplicationPoolIdentity'
    Reject {
        Assert-YfApplicationPoolProcessModel 'ApplicationPoolIdentity' $false 2
    } 'application pool web garden refused by maintenance guard' 'Deployment requires ApplicationPoolIdentity'
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

    $aclDirectory=Join-Path $root 'acl-directory'
    New-Item -ItemType Directory -Path $aclDirectory | Out-Null
    $broadDirectoryAcl=Get-Acl -LiteralPath $aclDirectory
    $broadDirectoryAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($usersSid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
    Set-Acl -LiteralPath $aclDirectory -AclObject $broadDirectoryAcl
    Set-YfApplicationDirectoryAcl $aclDirectory $readSid
    $directoryAcl=Get-Acl -LiteralPath $aclDirectory
    $directoryRules=@($directoryAcl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
    $directorySids=(@($directoryRules | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)) -join ','
    $fullControl=[Security.AccessControl.FileSystemRights]::FullControl
    $modify=[Security.AccessControl.FileSystemRights]::Modify
    $systemFull=@($directoryRules | Where-Object { $_.IdentityReference.Value -eq 'S-1-5-18' -and ($_.FileSystemRights -band $fullControl) -eq $fullControl })
    $adminsFull=@($directoryRules | Where-Object { $_.IdentityReference.Value -eq 'S-1-5-32-544' -and ($_.FileSystemRights -band $fullControl) -eq $fullControl })
    $identityModify=@($directoryRules | Where-Object { $_.IdentityReference -eq $readSid -and ($_.FileSystemRights -band $modify) -eq $modify -and
        ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::ChangePermissions) -eq 0 })
    if (!$directoryAcl.AreAccessRulesProtected -or $directorySids -ne ((@('S-1-5-18','S-1-5-20','S-1-5-32-544') | Sort-Object) -join ',') -or
        $systemFull.Count -lt 1 -or $adminsFull.Count -lt 1 -or $identityModify.Count -lt 1) {
        throw 'Application directory ACL is not limited to SYSTEM and Administrators (FullControl) plus the pool identity (Modify).'
    }
    Write-Output 'PASS storage and log directory ACL disables inheritance and keeps SYSTEM/Administrators FullControl'

    $dumpHelp="  --set-gtid-purged[=name]`r`n  --column-statistics  Add ANALYZE TABLE statements"
    $mysql8='mysqldump  Ver 8.0.36 for Win64 on x86_64 (MySQL Community Server - GPL)'
    $mysql57='mysqldump  Ver 10.13 Distrib 5.7.44, for Win64 (x86_64)'
    $maria='mysqldump  Ver 10.19 Distrib 10.6.16-MariaDB, for Win64 (AMD64)'
    $dumpCases=@(
        @{ Name='MySQL 8 client with 5.7 server'; Help=$dumpHelp; Client=$mysql8; Server='5.7.44-log'; Expected='--set-gtid-purged=OFF|--column-statistics=0' },
        @{ Name='MySQL 8 client with 8.0 server'; Help=$dumpHelp; Client=$mysql8; Server='8.0.36'; Expected='--set-gtid-purged=OFF' },
        @{ Name='MySQL 8 client with unknown server'; Help=$dumpHelp; Client=$mysql8; Server=''; Expected='--set-gtid-purged=OFF' },
        @{ Name='MySQL 5.7 client'; Help='  --set-gtid-purged[=name]'; Client=$mysql57; Server='5.7.44'; Expected='--set-gtid-purged=OFF' },
        @{ Name='MariaDB client'; Help='  --add-drop-table'; Client=$maria; Server='5.7.44'; Expected='' }
    )
    foreach ($case in $dumpCases) {
        $compatibility=Get-YfMySqlDumpCompatibilityArguments $case.Help $case.Client $case.Server
        $actual=$compatibility -join '|'
        if ($actual -ne $case.Expected) { throw "mysqldump compatibility arguments for $($case.Name): '$actual' instead of '$($case.Expected)'." }
    }
    Write-Output 'PASS mysqldump GTID and column-statistics options are added only when supported and needed'

    $listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
    $listener.Start(); $unusedPort=([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
    $healthMessage=$null
    try { Wait-YfHealth ('http://127.0.0.1:'+$unusedPort) 1 1 } catch { $healthMessage=$_.Exception.Message }
    if (!$healthMessage -or $healthMessage -notmatch 'Last error: \S' -or $healthMessage -match 'Last error: no response$' -or
        ([Net.ServicePointManager]::SecurityProtocol -band [Net.SecurityProtocolType]::Tls12) -eq 0) {
        throw "Health check failure did not keep the last error or enable TLS 1.2: $healthMessage"
    }
    Write-Output 'PASS health check enables TLS 1.2 and reports the last error'

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
        Reject { Read-YfMaintenanceConfig $path } "remote database $mode refused" 'Remote database maintenance requires SSL Mode=VerifyFull.'
    }

    $storageCa=Join-Path $storage 'database-ca.pem'
    [IO.File]::WriteAllText($storageCa,'fixture-ca-placeholder',(New-Object Text.UTF8Encoding($false)))
    Reject {
        Read-YfMaintenanceConfig (Write-TestConfig 'remote-storage-ca.json' 'db.example.test' 'VerifyFull' $storageCa)
    } 'database CA inside writable storage refused' 'Maintenance paths must not overlap.'
    $caFixture=Join-Path $root 'database-ca.pem'
    [IO.File]::WriteAllText($caFixture,'fixture-ca-placeholder',(New-Object Text.UTF8Encoding($false)))
    $remote=Read-YfMaintenanceConfig (Write-TestConfig 'remote-verify-full.json' 'db.example.test' 'VerifyFull' $caFixture)
    if ($remote.SslMode -ne 'VERIFY_IDENTITY' -or $remote.CaFile -ne $caFixture) {
        throw 'Remote VerifyFull or CA path was not preserved.'
    }
    Reject {
        Read-YfMaintenanceConfig (Write-TestConfig 'remote-missing-ca.json' 'db.example.test' 'VerifyFull' (Join-Path $root 'missing-ca.pem'))
    } 'missing database CA file refused' 'Database CA certificate file is missing.'
    Reject {
        Read-YfMaintenanceConfig (Write-TestConfig 'remote-client-certificate.json' 'db.example.test' 'VerifyFull' $caFixture -ClientCertificate)
    } 'unsupported client certificate settings refused' 'Client certificate connections need a separately configured backup client.'

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
