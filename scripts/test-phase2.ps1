[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
& $dotnet run --project (Join-Path $projectRoot 'tests\Phase2Tests') -c Release --no-launch-profile
if ($LASTEXITCODE -ne 0) { throw "第二阶段自动化测试失败（$LASTEXITCODE）。" }
