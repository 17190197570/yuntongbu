[CmdletBinding()]
param([Parameter(Mandatory)][Guid]$SourceConnectionId, [Parameter(Mandatory)][Guid]$TargetConnectionId,
      [Parameter(Mandatory)][string]$RelativePath, [Parameter(Mandatory)][string]$TargetDirectory, [switch]$AllowSourceDelete)
$ErrorActionPreference = 'Stop'
if (-not $AllowSourceDelete) { throw '此测试会在验证后删除指定的源测试文件。仅使用可丢弃的专用旧测试文件，显式传入 -AllowSourceDelete。' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
Push-Location $projectRoot
try {
    & $dotnet run --project (Join-Path $projectRoot 'tests\ArchiveNasTests') -c Release --no-launch-profile -- $SourceConnectionId $TargetConnectionId $RelativePath $TargetDirectory --allow-source-delete
    if ($LASTEXITCODE -ne 0) { throw '真实归档NAS检查未通过；不要删除归档日志、占位或任一副本。' }
} finally { Pop-Location }
