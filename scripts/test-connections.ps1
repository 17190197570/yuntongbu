[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$connectionDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $connectionDotnet)) { $connectionDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
& $connectionDotnet run --project (Join-Path $projectRoot 'tests\ConnectionMappingTests') -c Release --no-launch-profile
if ($LASTEXITCODE -ne 0) { throw 'NAS配置/文件夹映射测试失败或被系统策略阻止。此测试仅使用本地隔离目录，不代表真实NAS验收。' }
