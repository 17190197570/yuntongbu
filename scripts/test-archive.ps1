[CmdletBinding()]
param([switch]$Cfapi)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
Push-Location $projectRoot
try {
    $testArgs = @('run','--project',(Join-Path $projectRoot 'tests\ArchiveTests'),'-c','Release','--no-launch-profile','--')
    if ($Cfapi) { $testArgs += '--cfapi' }
    & $dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { throw '归档检查失败或被系统策略阻止；本地fixture不是双NAS验收。' }
} finally { Pop-Location }
