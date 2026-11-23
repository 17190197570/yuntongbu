[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
& $dotnet run --project (Join-Path $projectRoot 'tests\IndexedCfapiTests') -c Release --no-launch-profile -- (Join-Path $projectRoot 'artifacts\bin\CloudProviderHost.exe')
if ($LASTEXITCODE -ne 0) { throw "索引/管道/CFAPI 集成测试失败（$LASTEXITCODE）。" }
