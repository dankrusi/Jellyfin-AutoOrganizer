param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [string]$Version = '1.0.2',
    [string]$JellyfinVersion = '10.11.5',
    [string]$Repository = 'dankrusi/Jellyfin-AutoOrganizer',
    [string]$ManifestPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'manifest.json')
)
$ErrorActionPreference = 'Stop'
if ($Repository -notmatch '^[a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+$') { throw 'Invalid GitHub repository name.' }
$parsedVersion = [version]$Version
$pluginVersion = '{0}.{1}.{2}.{3}' -f $parsedVersion.Major, $parsedVersion.Minor, [Math]::Max(0, $parsedVersion.Build), [Math]::Max(0, $parsedVersion.Revision)
$targetAbi = ([version]("$JellyfinVersion.0")).ToString(4)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ArchivePath).Path)
try {
    foreach ($dll in @('Jellyfin.Plugin.AutoOrganizer.dll', 'AutoOrganizer.Core.dll')) {
        if ($null -eq $zip.GetEntry($dll)) { throw "Catalog archive must contain $dll at the ZIP root." }
    }
} finally { $zip.Dispose() }
$assetName = [IO.Path]::GetFileName($ArchivePath)
$versionEntry = [ordered]@{
    version = $pluginVersion
    changelog = 'Move mode now preserves remaining release files and removes empty source folders. Strip torrent-site prefixes from titles, add versioned settings URLs, and verify packaged DLL versions and embedded settings against release source.'
    targetAbi = $targetAbi
    sourceUrl = "https://github.com/$Repository/releases/download/v$Version/$assetName"
    checksum = (Get-FileHash -LiteralPath $ArchivePath -Algorithm MD5).Hash.ToLowerInvariant()
    timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
}
$previousVersions = @()
if (Test-Path -LiteralPath $ManifestPath) {
    $previous = @(Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json)
    if ($previous.Count -gt 0) {
        if ($previous[0].guid -ne '684d73d5-e50e-4e59-b643-d9d288531cce') { throw 'Unexpected plugin ID in manifest.' }
        $previousVersions = @($previous[0].versions | Where-Object { $_.version -ne $pluginVersion })
    }
}
$versions = @($versionEntry) + $previousVersions
$versions = @($versions | Sort-Object { [version]$_.version } -Descending)
$plugin = [ordered]@{
    guid = '684d73d5-e50e-4e59-b643-d9d288531cce'
    name = 'Auto Organizer'
    overview = 'Automatically organize completed movie and TV downloads.'
    description = 'Monitors an incoming folder and organizes torrent-style movies and series into Jellyfin Movies and Shows folders. Includes preview, copy/move modes, completion markers, subtitle support, and conflict protection. Configure folders after installation. Requires Jellyfin 10.11.5 or a compatible 10.11.x server.'
    owner = 'dankrusi'
    category = 'General'
    versions = $versions
}
$json = ConvertTo-Json -InputObject @($plugin) -Depth 20
[IO.File]::WriteAllText([IO.Path]::GetFullPath($ManifestPath), $json + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Updated $ManifestPath for $pluginVersion ($($versionEntry.checksum))"
