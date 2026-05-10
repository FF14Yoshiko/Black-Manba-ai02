param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$PluginMasterPath,

    [Parameter(Mandatory = $true)]
    [string]$RepoUrl,

    [Parameter(Mandatory = $true)]
    [string]$IconUrl,

    [string[]]$ImageUrls = @(),

    [long]$LastUpdateUnixSeconds = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
)

$ErrorActionPreference = "Stop"

$resolvedManifest = Resolve-Path -LiteralPath $ManifestPath
$resolvedPluginMaster = Resolve-Path -LiteralPath $PluginMasterPath

$manifest = Get-Content -LiteralPath $resolvedManifest.Path -Encoding UTF8 | ConvertFrom-Json
$pluginMaster = Get-Content -LiteralPath $resolvedPluginMaster.Path -Encoding UTF8 | ConvertFrom-Json

if ($pluginMaster -isnot [System.Collections.IEnumerable]) {
    throw "pluginmaster.json must be an array."
}

$entries = @($pluginMaster)
$target = $entries | Where-Object { $_.InternalName -eq $manifest.InternalName } | Select-Object -First 1
if ($null -eq $target) {
    $target = [ordered]@{}
    $entries = @($target) + $entries
}

$downloadLink = "${RepoUrl}/releases/latest/download/latest.zip"

$existingDescription = if ($null -ne $target.Description) { [string]$target.Description } else { "" }
$existingTags = @()
if ($null -ne $target.Tags) {
    $existingTags = @($target.Tags)
}

$target.Author = $manifest.Author
$target.Name = $manifest.Name
$target.Punchline = $manifest.Punchline
$target.Description = if ([string]::IsNullOrWhiteSpace($existingDescription)) { $manifest.Description } else { $existingDescription }
$target.InternalName = $manifest.InternalName
$target.AssemblyVersion = $manifest.AssemblyVersion
$target.ApplicableVersion = $manifest.ApplicableVersion
$target.DalamudApiLevel = $manifest.DalamudApiLevel
$target.IsHide = if ($null -ne $target.IsHide) { [bool]$target.IsHide } else { $false }
$target.IconUrl = $IconUrl
$target.ImageUrls = @($ImageUrls)
$target.RepoUrl = $RepoUrl
$target.DownloadLinkInstall = $downloadLink
$target.DownloadLinkUpdate = $downloadLink
$target.DownloadCount = if ($null -ne $target.DownloadCount) { [int]$target.DownloadCount } else { 0 }
$target.LastUpdate = $LastUpdateUnixSeconds
$target.Tags = @($existingTags + @($manifest.Tags) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)

$json = ConvertTo-Json -InputObject @($entries) -Depth 8
Set-Content -LiteralPath $resolvedPluginMaster.Path -Value $json -Encoding UTF8

Write-Host "pluginmaster.json synced: $($manifest.InternalName) $($manifest.AssemblyVersion)"
Write-Host "LastUpdate: $LastUpdateUnixSeconds"
