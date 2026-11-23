[CmdletBinding()]
param([switch]$Cfapi)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
Push-Location $projectRoot
try {
    $testArgs = @('run','--project',(Join-Path $projectRoot 'tests\Phase3Tests'),'-c','Release','--no-launch-profile','--')
    if ($Cfapi) { $testArgs += '--cfapi' }
    & $dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { throw '第三阶段首项测试未通过；本地fixture不等于NAS验收。' }
}
finally { Pop-Location }
