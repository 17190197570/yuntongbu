[CmdletBinding()]
param([switch]$Cfapi)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
Push-Location $projectRoot
try {
    $testArgs = @('run','--project',(Join-Path $projectRoot 'tests\CacheTests'),'-c','Release','--no-launch-profile','--')
    if ($Cfapi) { $testArgs += '--cfapi' }
    & $dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { throw '缓存检查失败或被系统策略阻止；本地 fixture 不代表真实 NAS 验收。' }
}
finally { Pop-Location }
