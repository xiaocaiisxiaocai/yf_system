[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AccessToken,

    [string]$BaseUrl = 'http://127.0.0.1:8080'
)

$ErrorActionPreference = 'Stop'
$requiredExtensions = @('pptx', 'mp4', 'webm', 'ogv', 'png', 'jpg', 'jpeg', 'gif', 'webp', 'bmp')
$baseUri = [Uri]$BaseUrl
if ($baseUri.Scheme -notin @('http', 'https')) {
    throw 'BaseUrl must use HTTP or HTTPS.'
}
if ($baseUri.Scheme -eq 'http' -and $baseUri.Host -notin @('127.0.0.1', 'localhost', '::1')) {
    throw 'A bearer token may use plain HTTP only with a loopback BaseUrl.'
}

$headers = @{ Authorization = "Bearer $AccessToken" }
$endpoint = $BaseUrl.TrimEnd('/') + '/api/v1/admin/system/configs'

function Get-ConfigSnapshot {
    $rows = @(Invoke-RestMethod -Method Get -Uri $endpoint -Headers $headers)
    $map = [ordered]@{}
    foreach ($row in ($rows | Sort-Object key)) {
        $map[[string]$row.key] = [string]$row.value
    }
    return $map
}

function Get-SnapshotHash([System.Collections.IDictionary]$snapshot) {
    $canonical = (($snapshot.Keys | ForEach-Object { "$_=$($snapshot[$_])" }) -join "`n")
    $bytes = [Text.Encoding]::UTF8.GetBytes($canonical)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

$before = Get-ConfigSnapshot
if (-not $before.Contains('upload.allowed_exts')) {
    throw 'The server did not return upload.allowed_exts.'
}
$current = @($before['upload.allowed_exts'].Split(',') | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
$unrestricted = [string]::IsNullOrWhiteSpace($before['upload.allowed_exts'])
$missing = if ($unrestricted) { @() } else { @($requiredExtensions | Where-Object { $_ -notin $current }) }
$target = @(($current + $missing) | Sort-Object -Unique)

if ($missing.Count -gt 0) {
    $body = @{ items = @(@{ key = 'upload.allowed_exts'; value = ($target -join ',') }) } | ConvertTo-Json -Depth 4 -Compress
    Invoke-RestMethod -Method Put -Uri $endpoint -Headers $headers -ContentType 'application/json' -Body $body | Out-Null
}

$after = Get-ConfigSnapshot
$otherKeys = @($before.Keys | Where-Object { $_ -ne 'upload.allowed_exts' })
$otherConfigsUnchanged = $otherKeys.Count -eq @($after.Keys | Where-Object { $_ -ne 'upload.allowed_exts' }).Count
foreach ($key in $otherKeys) {
    if (-not $after.Contains($key) -or $after[$key] -ne $before[$key]) {
        $otherConfigsUnchanged = $false
        break
    }
}
$actual = @($after['upload.allowed_exts'].Split(',') | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
$allRequiredPresent = $unrestricted -or @($requiredExtensions | Where-Object { $_ -notin $actual }).Count -eq 0
if (-not $otherConfigsUnchanged -or -not $allRequiredPresent) {
    throw 'Configuration verification failed after update.'
}

[ordered]@{
    baseUrl = $baseUri.GetLeftPart([UriPartial]::Authority)
    changed = $missing.Count -gt 0
    unrestricted = $unrestricted
    addedExtensions = $missing
    allowedBefore = $before['upload.allowed_exts']
    allowedAfter = $after['upload.allowed_exts']
    beforeConfigSha256 = Get-SnapshotHash $before
    afterConfigSha256 = Get-SnapshotHash $after
    otherConfigsUnchanged = $otherConfigsUnchanged
    allRequiredPresent = $allRequiredPresent
} | ConvertTo-Json -Depth 4
