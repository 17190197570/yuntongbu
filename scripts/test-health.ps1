[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$healthDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $healthDotnet)) { $healthDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
& $healthDotnet run --project (Join-Path $projectRoot 'tests\HealthTests') -c Release --no-launch-profile
if ($LASTEXITCODE -ne 0) { throw '健康检查测试失败或被系统策略阻止；故障注入不等于真实NAS验收。' }
