[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'check-public-source.ps1')
. (Join-Path $PSScriptRoot 'public-source-files.ps1')
[xml]$sourceProperties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$sourceVersion = [string]$sourceProperties.Project.PropertyGroup.Version
$sourceDelivery = Join-Path $projectRoot 'artifacts\OpenSource'
New-Item -ItemType Directory -Path $sourceDelivery -Force | Out-Null
$sourceArchive = Join-Path $sourceDelivery ("CloudDriveLite-$sourceVersion-OpenSource-" + [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '.zip')
$sourceFiles = @(Get-PublicSourceFiles -ProjectRoot $projectRoot | Sort-Object FullName)
$archiveStream = [IO.FileStream]::new($sourceArchive, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $archiveZip = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($sourceFile in $sourceFiles) {
            $sourceName = [IO.Path]::GetRelativePath($projectRoot, $sourceFile.FullName).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archiveZip, $sourceFile.FullName, $sourceName, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archiveZip.Dispose() }
} finally { $archiveStream.Dispose() }
$sourceHash = (Get-FileHash -LiteralPath $sourceArchive -Algorithm SHA256).Hash
$sourceHash + '  ' + [IO.Path]::GetFileName($sourceArchive) | Set-Content -LiteralPath ($sourceArchive + '.sha256') -Encoding utf8
Write-Host "Source-only package: $sourceArchive"
Write-Host "Files: $($sourceFiles.Count); SHA256: $sourceHash"
Write-Host 'No binaries, credentials, runtime databases or signing approval are included.'
