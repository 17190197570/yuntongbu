[CmdletBinding()]
param([switch]$Lifecycle)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$productDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $productDotnet)) { $productDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$productTestArgs = @('run','--project',(Join-Path $projectRoot 'tests\ProductTests'),'-c','Release','--no-launch-profile','--')
if ($Lifecycle) { $productTestArgs += '--lifecycle' }
& $productDotnet @productTestArgs
if ($LASTEXITCODE -ne 0) { throw '产品化测试失败或被系统策略阻止。' }
