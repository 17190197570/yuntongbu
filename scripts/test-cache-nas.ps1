[CmdletBinding()]
param([Parameter(Mandatory=$true)][Guid]$ConnectionId, [Parameter(Mandatory=$true)][string]$RelativePath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
Push-Location $projectRoot
try {
    & $dotnet run --project (Join-Path $projectRoot 'tests\CacheNasTests') -c Release --no-launch-profile -- $ConnectionId.ToString() $RelativePath
    if ($LASTEXITCODE -ne 0) { throw '真实 NAS 缓存验收未通过；保留文件及操作日志。' }
}
finally { Pop-Location }
