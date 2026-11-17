param(
    [string]$JellyfinVersion = '10.11.5',
    [string]$Version = '1.0.1',
    [switch]$Catalog
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repoRoot 'artifacts'
$stage = Join-Path $outputRoot ('package-' + [guid]::NewGuid().ToString('N'))
$folderName = "AutoOrganizer_$Version"
$pluginFolder = Join-Path $stage $folderName
New-Item -ItemType Directory -Force -Path $pluginFolder | Out-Null
dotnet publish (Join-Path $repoRoot 'src/Jellyfin.Plugin.AutoOrganizer/Jellyfin.Plugin.AutoOrganizer.csproj') -c Release --no-self-contained "-p:JellyfinVersion=$JellyfinVersion" "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
$publish = Join-Path $repoRoot 'src/Jellyfin.Plugin.AutoOrganizer/bin/Release/net9.0/publish'
foreach ($name in @('Jellyfin.Plugin.AutoOrganizer.dll', 'AutoOrganizer.Core.dll')) {
    Copy-Item -LiteralPath (Join-Path $publish $name) -Destination $pluginFolder
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $pluginFolder
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $pluginFolder
$suffix = if ($Catalog) { '-catalog' } else { '' }
$archive = Join-Path $outputRoot "AutoOrganizer-$Version-jellyfin-$JellyfinVersion$suffix.zip"
# Explicit forward-slash entries also work with Synology/Linux unzip tools when
# this script runs under Windows PowerShell 5, whose Compress-Archive uses '\'.
Add-Type -AssemblyName System.IO.Compression
$archiveStream = [IO.File]::Open($archive, [IO.FileMode]::Create)
try {
    $zip = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $pluginFolder -File) {
            $entryName = if ($Catalog) { $file.Name } else { "$folderName/$($file.Name)" }
            $entry = $zip.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            try {
                $inputStream = [IO.File]::OpenRead($file.FullName)
                try { $inputStream.CopyTo($entryStream) } finally { $inputStream.Dispose() }
            } finally { $entryStream.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $archiveStream.Dispose() }
$checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", "$checksum  $([IO.Path]::GetFileName($archive))`n", [Text.UTF8Encoding]::new($false))
Write-Host "Created $archive"
Write-Host "SHA256: $checksum"
