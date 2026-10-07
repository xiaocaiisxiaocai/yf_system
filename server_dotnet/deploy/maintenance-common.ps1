#Requires -Version 5.1
# Shared by install-iis.ps1 and maintain-iis.ps1; importing this file performs no deployment or maintenance.
Set-StrictMode -Version 2.0

function Assert-YfEnvironmentNames([string[]]$Names, [switch]$AllowConfigPath) {
    foreach ($name in $Names) {
        if ($name -match '^App(__|:)' -or (!$AllowConfigPath -and $name -eq 'YF_CONFIG_PATH')) {
            throw 'Deployment requires external JSON configuration without App environment overrides or inherited YF_CONFIG_PATH. Remove the override from the relevant scope before retrying.'
        }
    }
}
function Assert-YfApplicationPoolProcessModel([string]$IdentityType,[bool]$LoadUserProfile,[int]$MaxProcesses) {
    if ($IdentityType -ne 'ApplicationPoolIdentity' -or $LoadUserProfile -or $MaxProcesses -ne 1) {
        throw 'Deployment requires ApplicationPoolIdentity without a loaded user profile and exactly one worker process (maxProcesses=1).'
    }
}
function Assert-YfLaunch([string]$ProcessPath,[string]$Arguments,[string]$HostingModel) {
    if ($ProcessPath -ne 'dotnet' -or $Arguments.Trim() -notin @('.\Yf.Api.dll','Yf.Api.dll','".\Yf.Api.dll"','"Yf.Api.dll"') -or $HostingModel -ne 'inprocess') {
        throw 'Deployment requires the standard in-process dotnet Yf.Api.dll launch without additional command-line settings.'
    }
}
function Assert-YfPublishedConfig([string]$Root,[switch]$AllowConfigPath) {
    [xml]$xml=Get-Content -LiteralPath (Join-Path $Root 'web.config') -Raw -Encoding UTF8
    $nodes=@($xml.SelectNodes('//aspNetCore'))
    if ($nodes.Count -ne 1) { throw 'Exactly one ASP.NET Core launch configuration is required.' }
    $asp=$nodes[0]
    Assert-YfLaunch $asp.GetAttribute('processPath') $asp.GetAttribute('arguments') $asp.GetAttribute('hostingModel')
    Assert-YfEnvironmentNames @($asp.SelectNodes('environmentVariables/environmentVariable') | ForEach-Object { $_.GetAttribute('name') }) -AllowConfigPath:$AllowConfigPath
}
function Set-YfJsonMember($Object,[string]$Name,$Value) {
    if ($Object.PSObject.Properties[$Name]) { $Object.$Name = $Value }
    else { $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
}
function Clear-YfBundledSecrets([string]$Path) {
    # Clear only credentials and resource bindings; keep Logging, Serilog, AllowedHosts and
    # every other non-secret setting so the published defaults keep working.
    $raw = [IO.File]::ReadAllText($Path)
    $settings = if ([string]::IsNullOrWhiteSpace($raw)) { $null } else { $raw | ConvertFrom-Json }
    if ($null -eq $settings -or $settings -isnot [Management.Automation.PSCustomObject]) { $settings = New-Object PSObject }
    $app = if ($settings.PSObject.Properties['App']) { $settings.App } else { $null }
    if ($null -eq $app -or $app -isnot [Management.Automation.PSCustomObject]) { $app = New-Object PSObject; Set-YfJsonMember $settings 'App' $app }
    foreach ($name in @('ConnectionString','JwtSecret','StorageRoot','OemStorageRoot','BootstrapPassword','LogDirectory')) { Set-YfJsonMember $app $name '' }
    Set-YfJsonMember $app 'AutoInitializeDatabase' $false
    if ($app.PSObject.Properties['OemScanner']) { $app.PSObject.Properties.Remove('OemScanner') }
    if ($app.PSObject.Properties['Smtp'] -and $app.Smtp -is [Management.Automation.PSCustomObject] -and $app.Smtp.PSObject.Properties['Password']) {
        $app.Smtp.Password = ''
    }
    [IO.File]::WriteAllText($Path, (($settings | ConvertTo-Json -Depth 32) + "`n"), (New-Object Text.UTF8Encoding($false)))
}
function Set-YfExternalConfigurationFallback([string]$Root) {
    # A direct-bind package has usable appsettings.Production.json. Formal IIS installation
    # and maintenance use YF_CONFIG_PATH instead and must fail closed if it is lost.
    $settings = Join-Path $Root 'appsettings.json'
    if (!(Test-Path -LiteralPath $settings -PathType Leaf)) { throw 'Published appsettings.json is missing.' }
    foreach ($name in @('appsettings.json', 'appsettings.Production.json', 'appsettings.example.json')) {
        $environmentSettings = Join-Path $Root $name
        if (Test-Path -LiteralPath $environmentSettings -PathType Leaf) { Clear-YfBundledSecrets $environmentSettings }
    }
}
function Set-YfWebConfigEnvironment([string]$WebConfigPath,[string]$ExternalConfig) {
    # Replace, never append: publish-iis.ps1 already writes ASPNETCORE_ENVIRONMENT, and a
    # duplicate environmentVariable key makes IIS fail the whole site with HTTP 500.19.
    [xml]$xml = Get-Content -LiteralPath $WebConfigPath -Raw -Encoding UTF8
    $nodes = @($xml.SelectNodes('//aspNetCore'))
    if ($nodes.Count -ne 1) { throw 'Published ASP.NET Core IIS configuration missing.' }
    $asp = $nodes[0]
    $variableLists = @($asp.SelectNodes('environmentVariables'))
    if ($variableLists.Count -gt 1) {
        for ($i = 1; $i -lt $variableLists.Count; $i++) {
            foreach ($child in @($variableLists[$i].SelectNodes('environmentVariable'))) { $variableLists[0].AppendChild($child) | Out-Null }
            $asp.RemoveChild($variableLists[$i]) | Out-Null
        }
    }
    $variables = if ($variableLists.Count) { $variableLists[0] } else { $null }
    if (!$variables) { $variables = $xml.CreateElement('environmentVariables'); $asp.AppendChild($variables) | Out-Null }
    $wanted = [ordered]@{ YF_CONFIG_PATH = $ExternalConfig; ASPNETCORE_ENVIRONMENT = 'Production'; DOTNET_ENVIRONMENT = 'Production' }
    foreach ($name in $wanted.Keys) {
        foreach ($existing in @($variables.SelectNodes('environmentVariable'))) {
            if ([string]::Equals($existing.GetAttribute('name'), $name, [StringComparison]::OrdinalIgnoreCase)) { $variables.RemoveChild($existing) | Out-Null }
        }
        $node = $xml.CreateElement('environmentVariable'); $node.SetAttribute('name', $name); $node.SetAttribute('value', [string]$wanted[$name]); $variables.AppendChild($node) | Out-Null
    }
    $xml.Save($WebConfigPath)
}
function Assert-YfConfigurationEnvironment(
    [object[]]$AspNetCoreVariables,
    [string[]]$ApplicationPoolVariableNames,
    [string[]]$DefaultPoolVariableNames,
    [string[]]$MachineVariableNames,
    [string[]]$ProcessVariableNames,
    [string]$ExternalConfig=''
) {
    $allowConfigPath=![string]::IsNullOrWhiteSpace($ExternalConfig)
    $webVariables=@($AspNetCoreVariables | Where-Object { $null -ne $_ })
    Assert-YfEnvironmentNames @($webVariables | ForEach-Object { [string]$_.Name }) -AllowConfigPath:$allowConfigPath
    if ($allowConfigPath) {
        $effectivePaths=@($webVariables | Where-Object { [string]$_.Name -eq 'YF_CONFIG_PATH' })
        if ($effectivePaths.Count -ne 1 -or [string]$effectivePaths[0].Value -ne $ExternalConfig) {
            throw 'Effective IIS configuration does not match the external configuration file.'
        }
    }
    Assert-YfEnvironmentNames $ApplicationPoolVariableNames
    Assert-YfEnvironmentNames $DefaultPoolVariableNames
    Assert-YfEnvironmentNames $MachineVariableNames
    Assert-YfEnvironmentNames $ProcessVariableNames
}
function ConvertTo-YfConfigurationVariables($Collection) {
    return @($Collection | Where-Object { $null -ne $_ } | ForEach-Object { [pscustomobject]@{ Name=[string]$_['name']; Value=[string]$_['value'] } })
}
function Assert-YfInstallationConfiguration([string]$Name) {
    # A new site has no effective configuration to query yet. Validate every source it
    # will inherit before creating anything; the caller validates package web.config.
    Add-Type -Path (Join-Path $env:windir 'System32\inetsrv\Microsoft.Web.Administration.dll')
    $manager=New-Object Microsoft.Web.Administration.ServerManager
    try {
        $configuration=$manager.GetApplicationHostConfiguration()
        if (@($configuration.GetLocationPaths() | Where-Object {
            $_ -eq $Name -or $_.StartsWith($Name+'/',[StringComparison]::OrdinalIgnoreCase)
        }).Count) {
            throw 'A pre-existing IIS location configuration for the new site must be removed before installation.'
        }
        $asp=$configuration.GetSection('system.webServer/aspNetCore')
        $defaults=$configuration.GetSection('system.applicationHost/applicationPools').GetChildElement('applicationPoolDefaults')
        Assert-YfConfigurationEnvironment `
            -AspNetCoreVariables (ConvertTo-YfConfigurationVariables $asp.GetCollection('environmentVariables')) `
            -ApplicationPoolVariableNames @() `
            -DefaultPoolVariableNames @($defaults.GetCollection('environmentVariables') | ForEach-Object { [string]$_['name'] }) `
            -MachineVariableNames @([Environment]::GetEnvironmentVariables('Machine').Keys) `
            -ProcessVariableNames @(Get-ChildItem Env: | ForEach-Object { $_.Name })
    } finally { $manager.Dispose() }
}
function Assert-YfEffectiveConfiguration([string]$Name,[string]$Pool,[string]$ExternalConfig,[string]$Origin) {
    # Read inherited IIS settings as well as local web.config before stopping anything.
    Add-Type -Path (Join-Path $env:windir 'System32\inetsrv\Microsoft.Web.Administration.dll')
    $manager=New-Object Microsoft.Web.Administration.ServerManager
    try {
        $targetSite=$manager.Sites[$Name]
        $uri=[Uri]$Origin
        if ($targetSite.Applications.Count -ne 1 -or $uri.Scheme -ne 'https' -or $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or
            !@($targetSite.Bindings | Where-Object { $_.Protocol -eq 'https' -and $_.Host -eq $uri.DnsSafeHost -and $_.EndPoint.Port -eq $uri.Port }).Count) {
            throw 'Maintenance requires one root IIS application and an HTTPS binding matching the configured site origin.'
        }
        $applicationPool=$manager.ApplicationPools[$Pool]
        Assert-YfApplicationPoolProcessModel ($applicationPool.ProcessModel.IdentityType.ToString()) ([bool]$applicationPool.ProcessModel.LoadUserProfile) ([int]$applicationPool.ProcessModel.MaxProcesses)
        $asp=$manager.GetWebConfiguration($Name).GetSection('system.webServer/aspNetCore')
        Assert-YfLaunch ([string]$asp['processPath']) ([string]$asp['arguments']) ([string]$asp['hostingModel'])
        $variables=@($asp.GetCollection('environmentVariables'))
        $defaults=$manager.GetApplicationHostConfiguration().GetSection('system.applicationHost/applicationPools').GetChildElement('applicationPoolDefaults')
        Assert-YfConfigurationEnvironment `
            -AspNetCoreVariables (ConvertTo-YfConfigurationVariables $variables) `
            -ApplicationPoolVariableNames @($applicationPool.GetCollection('environmentVariables') | ForEach-Object { [string]$_['name'] }) `
            -DefaultPoolVariableNames @($defaults.GetCollection('environmentVariables') | ForEach-Object { [string]$_['name'] }) `
            -MachineVariableNames @([Environment]::GetEnvironmentVariables('Machine').Keys) `
            -ProcessVariableNames @(Get-ChildItem Env: | ForEach-Object { $_.Name }) `
            -ExternalConfig $ExternalConfig
    } finally { $manager.Dispose() }
}

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
function ConvertTo-YfSid($Identity) {
    if ($Identity -is [Security.Principal.SecurityIdentifier]) { return $Identity }
    return (New-Object Security.Principal.NTAccount ([string]$Identity)).Translate([Security.Principal.SecurityIdentifier])
}
function Get-YfAllowedApplicationSids([object[]]$AdditionalIdentities=@()) {
    $result = @('S-1-5-18','S-1-5-32-544')
    foreach ($identity in @($AdditionalIdentities)) {
        if ($null -ne $identity -and ![string]::IsNullOrWhiteSpace([string]$identity)) {
            $result += (ConvertTo-YfSid $identity).Value
        }
    }
    return @($result | Sort-Object -Unique)
}
function Test-YfSensitiveAccessRule($Rule) {
    if ($Rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { return $false }
    $mask = [Security.AccessControl.FileSystemRights]::Read -bor
        [Security.AccessControl.FileSystemRights]::Write -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    return ($Rule.FileSystemRights -band $mask) -ne 0
}
function Assert-YfNoUnexpectedApplicationTreeAccess([string]$Path,[object[]]$AllowedIdentities=@()) {
    if (!(Test-Path -LiteralPath $Path -PathType Container)) { throw 'Application data directory is missing.' }
    Assert-YfNoLinks $Path
    $allowed = Get-YfAllowedApplicationSids $AllowedIdentities
    $unexpected = New-Object Collections.Generic.List[string]
    $rootItem = Get-Item -LiteralPath $Path -Force
    $items = @($rootItem) + @(Get-ChildItem -LiteralPath $Path -Recurse -Force)
    foreach ($item in $items) {
        # The root's inherited rules are effective on the whole tree, while descendants only need
        # their explicit rules checked; inherited access was already checked at its originating ancestor.
        $includeInherited = $item.FullName -eq $rootItem.FullName
        $rules = @((Get-Acl -LiteralPath $item.FullName).GetAccessRules($true,$includeInherited,[Security.Principal.SecurityIdentifier]))
        foreach ($rule in $rules) {
            if ((Test-YfSensitiveAccessRule $rule) -and $allowed -notcontains $rule.IdentityReference.Value) {
                $unexpected.Add("$($item.FullName) [$($rule.IdentityReference.Value):$($rule.FileSystemRights)]")
                if ($unexpected.Count -ge 20) { break }
            }
        }
        if ($unexpected.Count -ge 20) { break }
    }
    if ($unexpected.Count) {
        throw ('Unexpected read/write ACL entries were found. No ACLs were changed. Review and remove or replace these entries before retrying: ' + ($unexpected -join '; '))
    }
}
function Set-YfApplicationDirectoryAcl([string]$Path, $ModifyIdentity) {
    # Business storage and log directories: inheritance disabled, SYSTEM and BUILTIN\Administrators
    # keep FullControl (admins are never locked out), the application pool identity gets Modify,
    # and every other inherited or explicit ACE on the directory itself is dropped. Child items
    # re-inherit these rules; explicit ACEs already set on child items are left as they are.
    if (!(Test-Path -LiteralPath $Path -PathType Container)) { throw 'Application directory is missing.' }
    Assert-YfNoLinks $Path
    $modifySid = ConvertTo-YfSid $ModifyIdentity
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    foreach ($sid in @((New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'),(New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'))) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl',$inherit,'None','Allow')))
    }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($modifySid,'Modify',$inherit,'None','Allow')))
    Set-Acl -LiteralPath $Path -AclObject $acl
    Assert-YfApplicationDirectoryAcl $Path $modifySid
}
function Assert-YfApplicationDirectoryAcl([string]$Path, $ModifyIdentity) {
    $modifySid = ConvertTo-YfSid $ModifyIdentity
    $allowed = @('S-1-5-18','S-1-5-32-544',$modifySid.Value)
    $applied = Get-Acl -LiteralPath $Path
    $rules = @($applied.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
    $modifyMask = [Security.AccessControl.FileSystemRights]::Modify
    $identityRules = @($rules | Where-Object { $_.IdentityReference -eq $modifySid -and $_.AccessControlType -eq 'Allow' -and ($_.FileSystemRights -band $modifyMask) -eq $modifyMask })
    $unexpected = @($rules | Where-Object { $allowed -notcontains $_.IdentityReference.Value })
    if (!$applied.AreAccessRulesProtected -or $identityRules.Count -lt 1 -or $unexpected.Count) {
        throw 'Application directory ACL verification failed; only SYSTEM, Administrators and the application pool identity may have access.'
    }
}
function Set-YfApplicationDirectoryReadAcl([string]$Path, $ReadIdentity) {
    if (!(Test-Path -LiteralPath $Path -PathType Container)) { throw 'Application directory is missing.' }
    Assert-YfNoLinks $Path
    $readSid = ConvertTo-YfSid $ReadIdentity
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    $rootAcl = New-Object Security.AccessControl.DirectorySecurity
    $rootAcl.SetAccessRuleProtection($true,$false)
    foreach ($sid in @((New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'),(New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'))) {
        $rootAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl',$inherit,'None','Allow')))
    }
    $rootAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($readSid,'ReadAndExecute',$inherit,'None','Allow')))
    Set-Acl -LiteralPath $Path -AclObject $rootAcl

    # Package copies must never carry or inherit write access. Clear every descendant's explicit
    # access rules and make it inherit the protected root. Ownership and audit rules are preserved.
    foreach ($item in Get-ChildItem -LiteralPath $Path -Recurse -Force) {
        $acl = Get-Acl -LiteralPath $item.FullName
        foreach ($rule in @($acl.GetAccessRules($true,$false,[Security.Principal.SecurityIdentifier]))) {
            [void]$acl.RemoveAccessRuleSpecific($rule)
        }
        $acl.SetAccessRuleProtection($false,$false)
        Set-Acl -LiteralPath $item.FullName -AclObject $acl
    }
    Assert-YfApplicationDirectoryReadAcl $Path $readSid
}
function Assert-YfApplicationDirectoryReadAcl([string]$Path, $ReadIdentity) {
    $readSid = ConvertTo-YfSid $ReadIdentity
    Assert-YfNoUnexpectedApplicationTreeAccess $Path @($readSid)
    $root = Get-Acl -LiteralPath $Path
    $rules = @($root.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
    $writeMask = [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    $executeMask = [Security.AccessControl.FileSystemRights]::ReadAndExecute
    $fullControl = [Security.AccessControl.FileSystemRights]::FullControl
    $systemFull = @($rules | Where-Object { $_.IdentityReference.Value -eq 'S-1-5-18' -and $_.AccessControlType -eq 'Allow' -and ($_.FileSystemRights -band $fullControl) -eq $fullControl })
    $adminsFull = @($rules | Where-Object { $_.IdentityReference.Value -eq 'S-1-5-32-544' -and $_.AccessControlType -eq 'Allow' -and ($_.FileSystemRights -band $fullControl) -eq $fullControl })
    $readRules = @($rules | Where-Object { $_.IdentityReference -eq $readSid -and $_.AccessControlType -eq 'Allow' })
    if (!$root.AreAccessRulesProtected -or !$systemFull.Count -or !$adminsFull.Count -or $readRules.Count -lt 1 -or
        @($readRules | Where-Object { ($_.FileSystemRights -band $writeMask) -ne 0 }).Count -or
        @($readRules | Where-Object { ($_.FileSystemRights -band $executeMask) -eq $executeMask -and ($_.FileSystemRights -band $writeMask) -eq 0 }).Count -lt 1) {
        throw 'Application directory ACL verification failed; the application pool must have inherited ReadAndExecute without write access.'
    }
    foreach ($item in Get-ChildItem -LiteralPath $Path -Recurse -Force) {
        $itemRules = @((Get-Acl -LiteralPath $item.FullName).GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
        if (@($itemRules | Where-Object { $_.IdentityReference -eq $readSid -and $_.AccessControlType -eq 'Allow' -and ($_.FileSystemRights -band $writeMask) -ne 0 }).Count) {
            throw "Application payload grants write access to the application pool identity: $($item.FullName)"
        }
    }
}
function Wait-YfHealth([string]$Origin,[int]$WaitSeconds,[int]$RequestTimeoutSeconds) {
    # Windows PowerShell 5.1 may default to TLS 1.0/1.1; IIS sites commonly require TLS 1.2+.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $url = $Origin.TrimEnd('/') + '/health'
    $lastError = 'no response'
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    do {
        try {
            $health = Invoke-RestMethod -Uri $url -TimeoutSec $RequestTimeoutSeconds -UseBasicParsing
            if ($health -is [Management.Automation.PSCustomObject] -and $health.PSObject.Properties['status'] -and $health.PSObject.Properties['db'] -and
                $health.status -eq 'ok' -and $health.db -eq 'up') { return }
            $text = [string]($health | ConvertTo-Json -Compress -Depth 3)
            if ($text.Length -gt 200) { $text = $text.Substring(0,200) + '...' }
            $lastError = 'unexpected /health response: ' + $text
        } catch {
            $lastError = $_.Exception.Message
        }
        if ([DateTime]::UtcNow -ge $deadline) { break }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "HTTPS health check of $url did not report status=ok, db=up within $WaitSeconds seconds. Last error: $lastError"
}
function Test-YfAspNetRuntime([string]$ApplicationRoot) {
    # The package's own runtimeconfig decides the required shared frameworks.
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $ApplicationRoot 'Yf.Api.runtimeconfig.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $frameworks = @()
    if ($runtimeConfig.runtimeOptions.PSObject.Properties['frameworks']) { $frameworks += @($runtimeConfig.runtimeOptions.frameworks) }
    if ($runtimeConfig.runtimeOptions.PSObject.Properties['framework']) { $frameworks += $runtimeConfig.runtimeOptions.framework }
    if (!$frameworks.Count) { throw 'Package runtimeconfig does not name a shared framework.' }
    $installed = @(& dotnet --list-runtimes)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to list installed .NET runtimes.' }
    foreach ($framework in $frameworks) {
        $major = ([string]$framework.version).Split('.')[0]
        if (!($installed -match ('^' + [regex]::Escape([string]$framework.name) + ' ' + $major + '\.'))) {
            throw "Required runtime $($framework.name) $($framework.version) is not installed."
        }
    }
}
function Test-YfStorageReadWrite([string]$Path) {
    $probe = Join-Path $Path ('.yf-maintenance-probe-' + [guid]::NewGuid().ToString('N') + '.tmp')
    $content = [Text.Encoding]::ASCII.GetBytes('yf-maintenance-readiness')
    try {
        [IO.File]::WriteAllBytes($probe, $content)
        $read = [IO.File]::ReadAllBytes($probe)
        if ([Convert]::ToBase64String($read) -ne [Convert]::ToBase64String($content)) { throw 'mismatch' }
    } catch {
        throw 'Storage read/write probe failed; check the storage path and disk.'
    } finally {
        if (Test-Path -LiteralPath $probe -PathType Leaf) { Remove-Item -LiteralPath $probe -Force }
    }
}
function Assert-YfDeploymentReadiness([string]$ApplicationRoot,$Config,[string]$ApplicationIdentity) {
    # Offline pre-check of a NEW application directory and its external configuration before any
    # schema migration or file conversion. Pending EF migrations are expected at this point, so the
    # database schema is not validated here; the post-start /health check covers the final state.
    foreach ($required in @('Yf.Api.dll','Yf.Api.runtimeconfig.json','web.config','wwwroot\index.html')) {
        if (!(Test-Path -LiteralPath (Join-Path $ApplicationRoot $required) -PathType Leaf)) { throw "Readiness: application payload missing $required." }
    }
    $app = $Config.Config.App
    foreach ($name in @('ConnectionString','JwtSecret','StorageRoot','WebBaseUrl')) {
        if (!$app.PSObject.Properties[$name] -or [string]::IsNullOrWhiteSpace([string]$app.$name)) { throw "Readiness: App.$name is required in the external configuration." }
    }
    if ([Text.Encoding]::UTF8.GetByteCount([string]$app.JwtSecret) -lt 32) { throw 'Readiness: App.JwtSecret must be at least 32 bytes.' }
    if (!(Test-Path -LiteralPath $Config.Storage -PathType Container)) { throw 'Readiness: storage directory is missing.' }
    Assert-YfNoLinks $Config.Storage
    Test-YfStorageReadWrite $Config.Storage
    if ($Config.OemStorage) {
        if (!(Test-Path -LiteralPath $Config.OemStorage -PathType Container)) { throw 'Readiness: configured App.OemStorageRoot is missing.' }
        Assert-YfNoLinks $Config.OemStorage
        Test-YfStorageReadWrite $Config.OemStorage
    }
    if ($ApplicationIdentity) {
        $sid = ConvertTo-YfSid $ApplicationIdentity
        $modifyMask = [Security.AccessControl.FileSystemRights]::Modify
        $grants = @((Get-Acl -LiteralPath $Config.Storage).GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) | Where-Object {
            $_.IdentityReference -eq $sid -and $_.AccessControlType -eq 'Allow' -and ($_.FileSystemRights -band $modifyMask) -eq $modifyMask })
        if (!$grants.Count) { throw 'Readiness: the application pool identity has no Modify permission on storage.' }
        if ($Config.OemStorage) {
            $oemGrants = @((Get-Acl -LiteralPath $Config.OemStorage).GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) | Where-Object {
                $_.IdentityReference -eq $sid -and $_.AccessControlType -eq 'Allow' -and ($_.FileSystemRights -band $modifyMask) -eq $modifyMask })
            if (!$oemGrants.Count) { throw 'Readiness: the application pool identity has no Modify permission on OEM storage.' }
        }
    }
    $logDirectory = Get-YfLogDirectory $Config
    if ($logDirectory) {
        if (!(Test-Path -LiteralPath $logDirectory -PathType Container)) { throw 'Readiness: configured App.LogDirectory is missing.' }
        Test-YfStorageReadWrite $logDirectory
    }
    Test-YfAspNetRuntime $ApplicationRoot
}
function Get-YfLogDirectory($Config) {
    $app = $Config.Config.App
    if (!$app.PSObject.Properties['LogDirectory'] -or [string]::IsNullOrWhiteSpace([string]$app.LogDirectory)) { return '' }
    return Get-YfFullPath ([string]$app.LogDirectory)
}
function Protect-YfConfigurationFile([string]$Path, $ReadIdentity) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Configuration file is missing.' }
    $readSid = if ($ReadIdentity -is [Security.Principal.SecurityIdentifier]) {
        $ReadIdentity
    } else {
        (New-Object Security.Principal.NTAccount ([string]$ReadIdentity)).Translate([Security.Principal.SecurityIdentifier])
    }
    $acl = New-Object Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true,$false)
    $fullControlSids = @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User,
        (New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'),
        (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544')
    ) | Select-Object -Unique
    foreach ($sid in $fullControlSids) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','Allow')
        $acl.AddAccessRule($rule)
    }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($readSid,'Read','Allow')))
    Set-Acl -LiteralPath $Path -AclObject $acl

    $applied = Get-Acl -LiteralPath $Path
    $writeMask = [Security.AccessControl.FileSystemRights]::WriteData -bor [Security.AccessControl.FileSystemRights]::AppendData -bor [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor [Security.AccessControl.FileSystemRights]::WriteAttributes -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    $readRules = @($applied.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) | Where-Object {
        $_.IdentityReference -eq $readSid -and $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow
    })
    if (!$applied.AreAccessRulesProtected -or $readRules.Count -ne 1 -or
        ($readRules[0].FileSystemRights -band $writeMask) -ne 0 -or
        ($readRules[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::Read) -ne [Security.AccessControl.FileSystemRights]::Read) {
        throw 'Configuration ACL verification failed; the application identity must have read-only access.'
    }
}
function Write-YfJson([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 12),(New-Object Text.UTF8Encoding($false)))
}
function Get-YfDbOption($Builder,[string[]]$Names,[string]$Default='') {
    foreach ($name in $Names) { if ($Builder.ContainsKey($name)) { return [string]$Builder[$name] } }
    return $Default
}
function Test-YfLoopbackDatabaseServer([string]$Server) {
    return $Server -in @('localhost','127.0.0.1','::1','[::1]')
}
function Read-YfMaintenanceConfig([string]$Path) {
    $Path = Get-YfFullPath $Path
    Assert-YfNoLinks $Path
    try {
        $config = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        $builder = New-Object System.Data.Common.DbConnectionStringBuilder
        $builder.set_ConnectionString($config.App.ConnectionString)
        $storage = Get-YfFullPath $config.App.StorageRoot
        $oemStorage = $null
        if ($config.App.PSObject.Properties['OemStorageRoot'] -and ![string]::IsNullOrWhiteSpace([string]$config.App.OemStorageRoot)) {
            $oemStorage = Get-YfFullPath ([string]$config.App.OemStorageRoot)
            Assert-YfNoLinks $oemStorage
            Assert-YfSeparate @($Path,$storage,$oemStorage)
        }
        $database = Get-YfDbOption $builder @('Database','Initial Catalog')
        $server = Get-YfDbOption $builder @('Server','Host','Data Source','DataSource','Address','Addr','Network Address') 'localhost'
        $port = [int](Get-YfDbOption $builder @('Port') '3306')
        $user = Get-YfDbOption $builder @('User ID','UserID','User Id','User','Uid','Username')
        $password = Get-YfDbOption $builder @('Password','Pwd')
        $protocol = Get-YfDbOption $builder @('Connection Protocol','ConnectionProtocol','Protocol') 'Sockets'
        $ssl = Get-YfDbOption $builder @('SSL Mode','SslMode') 'Preferred'
        $sslModes = @{ None='DISABLED'; Disabled='DISABLED'; Preferred='PREFERRED'; Required='REQUIRED'; VerifyCA='VERIFY_CA'; VerifyFull='VERIFY_IDENTITY' }
        if ($database -notmatch '^[A-Za-z0-9_]+$' -or !$user -or $server -notmatch '^[A-Za-z0-9_.:-]+$' -or $port -lt 1 -or $port -gt 65535 -or $protocol -notin @('Sockets','Socket','TCP','Tcp') -or !$sslModes.ContainsKey($ssl)) { throw 'Unsupported database settings.' }
        if (!(Test-YfLoopbackDatabaseServer $server) -and $ssl -ne 'VerifyFull') {
            throw 'Remote database maintenance requires SSL Mode=VerifyFull.'
        }
        $caFile = Get-YfDbOption $builder @('CACertificateFile','CA Certificate File','SslCa','SSL CA')
        if ($caFile) {
            $caFile = Get-YfFullPath $caFile
            Assert-YfNoLinks $caFile
            if (!(Test-Path -LiteralPath $caFile -PathType Leaf)) { throw 'Database CA certificate file is missing.' }
            $caPaths = @($Path,$storage,$caFile)
            if ($oemStorage) { $caPaths += $oemStorage }
            Assert-YfSeparate $caPaths
        }
        foreach ($key in @('CertificateFile','Certificate File','CertificatePassword','Certificate Password','SslCert','SslKey')) {
            if ($builder.ContainsKey($key)) { throw 'Client certificate connections need a separately configured backup client.' }
        }
        $origin = [Uri]$config.App.WebBaseUrl
        if (!$origin.IsAbsoluteUri -or $origin.Scheme -notin @('http','https')) { throw 'Invalid origin.' }
    } catch {
        # Messages thrown above never contain configuration values; anything else (JSON parser,
        # connection-string builder) could echo credentials and is replaced by a generic message.
        $safeMessages = @('Unsupported database settings.','Remote database maintenance requires SSL Mode=VerifyFull.',
            'Database CA certificate file is missing.','Client certificate connections need a separately configured backup client.',
            'Maintenance paths must not overlap.','Reparse points are not allowed in maintenance paths.','Invalid origin.',
            'Use an absolute local disk path.','Drive roots are not allowed.')
        if ($safeMessages -contains $_.Exception.Message) { throw ('Unable to read maintenance configuration: ' + $_.Exception.Message) }
        throw 'Unable to read maintenance configuration. Check paths, TCP database settings and JSON; credentials are not printed.'
    }
    return [pscustomobject]@{ Path=$Path; Storage=$storage; OemStorage=$oemStorage; Database=$database; Server=$server; Port=$port; User=$user; Password=$password; SslMode=$sslModes[$ssl]; CaFile=$caFile; Origin=$config.App.WebBaseUrl; Config=$config }
}
function ConvertTo-YfMySqlOption([string]$Value) {
    return '"'+$Value.Replace('\','\\').Replace('"','\"').Replace("`r",'\r').Replace("`n",'\n')+'"'
}
function Write-YfMySqlDefaults($Config,[string]$Directory) {
    $path = Join-Path $Directory ('.mysql-'+[guid]::NewGuid().ToString('N')+'.cnf')
    $lines = @('[client]', ('host='+(ConvertTo-YfMySqlOption $Config.Server)), ('port='+$Config.Port), ('user='+(ConvertTo-YfMySqlOption $Config.User)), ('password='+(ConvertTo-YfMySqlOption $Config.Password)), ('ssl-mode='+$Config.SslMode), 'protocol=TCP', 'default-character-set=utf8mb4')
    if ($Config.CaFile) { $lines += 'ssl-ca='+(ConvertTo-YfMySqlOption $Config.CaFile) }
    try {
        [IO.File]::WriteAllLines($path,$lines,(New-Object Text.UTF8Encoding($false)))
        return $path
    } catch {
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
        throw
    }
}
function Invoke-YfNativeCapture([string]$FilePath,[string[]]$Arguments,[string]$StderrPath) {
    $previousPreference=$ErrorActionPreference
    try {
        # Windows PowerShell 5.1 turns native stderr into NativeCommandError when
        # ErrorActionPreference is Stop. Capture it and decide from the exit code.
        $ErrorActionPreference='Continue'
        $output=@(& $FilePath @Arguments 2> $StderrPath)
        $exitCode=$LASTEXITCODE
    } finally {
        $ErrorActionPreference=$previousPreference
    }
    return [pscustomobject]@{ExitCode=$exitCode;Output=$output}
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
function Get-YfMySqlMajorMinor([string]$VersionText) {
    # "mysqldump  Ver 8.0.36 for Win64", "mysqldump  Ver 10.13 Distrib 5.7.44, for Win64", "5.7.44-log"
    $match = [regex]::Match([string]$VersionText,'Distrib\s+(\d+)\.(\d+)')
    if (!$match.Success) { $match = [regex]::Match([string]$VersionText,'Ver\s+(\d+)\.(\d+)') }
    if (!$match.Success) { $match = [regex]::Match([string]$VersionText,'^\s*(\d+)\.(\d+)') }
    if (!$match.Success) { return $null }
    return [version]("{0}.{1}" -f $match.Groups[1].Value,$match.Groups[2].Value)
}
function Get-YfMySqlDumpCompatibilityArguments([string]$HelpText,[string]$ClientVersionText,[string]$ServerVersionText) {
    # --set-gtid-purged=OFF keeps a GTID-enabled source from writing SET @@GLOBAL.GTID_PURGED into the
    # dump (restoring it needs SUPER and fails on a target with its own GTID history). MariaDB and very
    # old clients do not know the option. --column-statistics=0 is needed only when a MySQL 8 client
    # dumps a 5.7 server, which has no information_schema.COLUMN_STATISTICS.
    $arguments = @()
    if ($HelpText -match '--set-gtid-purged') { $arguments += '--set-gtid-purged=OFF' }
    $client = Get-YfMySqlMajorMinor $ClientVersionText
    $server = Get-YfMySqlMajorMinor $ServerVersionText
    if ($HelpText -match '--column-statistics' -and $client -and $client.Major -ge 8 -and $ClientVersionText -notmatch 'MariaDB' -and
        $server -and $server -lt [version]'8.0') {
        $arguments += '--column-statistics=0'
    }
    return ,$arguments
}
function Get-YfServerVersion($Config,[string]$Defaults,[string]$MySql,[string]$LogDirectory) {
    # Best effort: without a usable mysql client the server version stays unknown.
    if ([string]::IsNullOrWhiteSpace($MySql) -or !(Get-Command $MySql -ErrorAction SilentlyContinue)) { return '' }
    $probe = Invoke-YfNativeCapture $MySql @("--defaults-file=$Defaults",'--batch','--skip-column-names','--execute=SELECT VERSION()') (Join-Path $LogDirectory 'server-version.stderr.log')
    if ($probe.ExitCode -ne 0 -or @($probe.Output).Count -ne 1) { return '' }
    return ([string]@($probe.Output)[0]).Trim()
}
function New-YfBackup([string]$ApplicationRoot,$Config,[string]$Destination,[string]$MySqlDump,[string]$SiteName,[string]$MySql='') {
    $ApplicationRoot = Get-YfFullPath $ApplicationRoot
    $Destination = Get-YfFullPath $Destination
    $paths = @($ApplicationRoot,$Config.Storage,$Config.Path,$Destination)
    if ($Config.OemStorage) { $paths += $Config.OemStorage }
    if ($Config.CaFile) { $paths += $Config.CaFile }
    Assert-YfSeparate $paths
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
        $help=Invoke-YfNativeCapture $MySqlDump @('--help') (Join-Path $Destination 'dump-help.stderr.log')
        $clientVersion=Invoke-YfNativeCapture $MySqlDump @('--version') (Join-Path $Destination 'dump-version.stderr.log')
        $serverVersion=Get-YfServerVersion $Config $defaults $MySql $Destination
        $compatibility=Get-YfMySqlDumpCompatibilityArguments (@($help.Output) -join "`n") (@($clientVersion.Output) -join "`n") $serverVersion
        # --events requires the EVENT privilege on the database (see deploy/README.md).
        $dumpArguments=@("--defaults-file=$defaults",'--single-transaction','--routines','--triggers','--events','--hex-blob','--no-tablespaces') + $compatibility + @("--result-file=$sql",$Config.Database)
        $dump=Invoke-YfNativeCapture $MySqlDump $dumpArguments (Join-Path $Destination 'dump.stderr.log')
        if ($dump.ExitCode -ne 0 -or !(Test-Path -LiteralPath $sql) -or (Get-Item -LiteralPath $sql).Length -eq 0) { throw 'Database backup failed. Inspect the protected backup log.' }
    } finally {
        if ($defaults -and (Test-Path -LiteralPath $defaults -PathType Leaf)) { Remove-Item -LiteralPath $defaults -Force }
    }
    Copy-YfTree $ApplicationRoot (Join-Path $Destination 'application')
    Copy-YfTree $Config.Storage (Join-Path $Destination 'storage')
    Copy-Item -LiteralPath $Config.Path -Destination (Join-Path $Destination 'configuration.json')
    $manifest = [ordered]@{kind='yf-offline-backup';schemaVersion=1;createdUtc=[DateTime]::UtcNow.ToString('o');siteName=$SiteName;database=$Config.Database;containsSecrets=$true;protection='restricted-acl';files=@(Get-YfManifestFiles $Destination)}
    Write-YfJson (Join-Path $Destination 'manifest.json') $manifest
    Assert-YfManifest $Destination 'yf-offline-backup' | Out-Null
}
function Restore-YfBackup([string]$BackupRoot,$Config,[string]$NewApplicationRoot,[string]$MySql,[switch]$ReuseOemStorage) {
    $BackupRoot = Get-YfFullPath $BackupRoot
    $NewApplicationRoot = Get-YfFullPath $NewApplicationRoot
    $manifest = Assert-YfManifest $BackupRoot 'yf-offline-backup'
    foreach ($name in @('database.sql','configuration.json','application\Yf.Api.dll','application\web.config','application\wwwroot\index.html')) {
        if (!(Test-Path -LiteralPath (Join-Path $BackupRoot $name) -PathType Leaf)) { throw 'Required backup payload is missing.' }
    }
    $paths = @($BackupRoot,$Config.Storage,$Config.Path,$NewApplicationRoot)
    if ($Config.OemStorage) { $paths += $Config.OemStorage }
    if ($Config.CaFile) { $paths += $Config.CaFile }
    Assert-YfSeparate $paths
    Assert-YfEmptyDirectory $NewApplicationRoot
    Assert-YfEmptyDirectory $Config.Storage
    if ($ReuseOemStorage -and !$Config.OemStorage) { throw 'ReuseOemStorage requires a configured App.OemStorageRoot.' }
    # OEM files are never backed up. A reused root keeps its content and is reconciled
    # against the restored database; otherwise the restore starts from a new empty root.
    if ($Config.OemStorage -and !$ReuseOemStorage) { Assert-YfEmptyDirectory $Config.OemStorage }
    $scratch = Join-Path ([IO.Path]::GetTempPath()) ('yf-restore-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scratch | Out-Null
    Protect-YfDirectory $scratch
    $defaults = $null
    try {
        $defaults = Write-YfMySqlDefaults $Config $scratch
        $probe=Invoke-YfNativeCapture $MySql @("--defaults-file=$defaults",'--batch','--skip-column-names',"--database=$($Config.Database)",'--execute=SELECT (SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE())+(SELECT COUNT(*) FROM information_schema.routines WHERE routine_schema=DATABASE())+(SELECT COUNT(*) FROM information_schema.events WHERE event_schema=DATABASE())') (Join-Path $scratch 'probe.stderr.log')
        $count=@($probe.Output)
        if ($probe.ExitCode -ne 0 -or $count.Count -ne 1 -or $count[0].Trim() -ne '0') { throw 'Restore requires an existing empty database; no data was overwritten.' }
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
        $oemTableProbe = Invoke-YfNativeCapture $MySql @("--defaults-file=$defaults",'--batch','--skip-column-names',"--database=$($Config.Database)","--execute=SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='system_configs'") (Join-Path $scratch 'oem-probe.stderr.log')
        if ($oemTableProbe.ExitCode -ne 0 -or @($oemTableProbe.Output).Count -ne 1) { throw 'Unable to inspect the restored database for OEM reconciliation.' }
        if (([string]@($oemTableProbe.Output)[0]).Trim() -eq '1') {
            $oemMarker = Invoke-YfNativeCapture $MySql @("--defaults-file=$defaults",'--batch','--skip-column-names',("--database="+$Config.Database),"--execute=UPDATE system_configs SET cfg_value='RESTORED' WHERE cfg_key='oem.storage.reconcile_required'; SELECT COUNT(*), COALESCE(SUM(cfg_value='RESTORED'),0) FROM system_configs WHERE cfg_key='oem.storage.reconcile_required'") (Join-Path $scratch 'oem-marker.stderr.log')
            $markerState = if ($oemMarker.ExitCode -eq 0 -and @($oemMarker.Output).Count -eq 1) { ([string]@($oemMarker.Output)[0]).Trim() -split '\s+' } else { @() }
            if ($markerState.Count -ne 2 -or ($markerState[0] -ne '0' -and $markerState[1] -ne '1')) { throw 'Unable to mark OEM storage for reconciliation after restore.' }
            # A backup taken before the OEM schema existed has no marker row and no OEM records to reconcile.
            if ($markerState[0] -eq '0' -and $ReuseOemStorage) { throw 'The backup predates the OEM schema; ReuseOemStorage cannot be reconciled against it.' }
        } elseif ($ReuseOemStorage) {
            throw 'The backup predates the OEM schema; ReuseOemStorage cannot be reconciled against it.'
        }
        Copy-YfTree (Join-Path $BackupRoot 'storage') $Config.Storage
        Copy-YfTree (Join-Path $BackupRoot 'application') $NewApplicationRoot
    } finally {
        if ($defaults -and (Test-Path -LiteralPath $defaults -PathType Leaf)) { Remove-Item -LiteralPath $defaults -Force }
        # Only exact owned scratch files; no recursive cleanup of caller paths.
        foreach ($file in Get-ChildItem -LiteralPath $scratch -File) { Remove-Item -LiteralPath $file.FullName -Force }
        Remove-Item -LiteralPath $scratch
    }
}
