[CmdletBinding()]
param([switch]$Build)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if ($Build) { & (Join-Path $PSScriptRoot 'build.ps1'); if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }
$agent = Join-Path $projectRoot 'artifacts\bin\CloudDrive.Agent.exe'
$portableDotNet = Join-Path $projectRoot '.tools\dotnet'
if (Test-Path -LiteralPath (Join-Path $portableDotNet 'dotnet.exe')) { $env:DOTNET_ROOT = $portableDotNet }
if (-not (Test-Path -LiteralPath $agent)) { throw '请先运行 scripts\build.ps1。' }
$source = Join-Path $projectRoot '.demo\source'
$syncRoot = Join-Path $projectRoot '.demo\cloud'
New-Item -ItemType Directory -Path $source, $syncRoot -Force | Out-Null
$sample = Join-Path $source '欢迎.txt'
if (-not (Test-Path -LiteralPath $sample)) {
    "CloudDrive Lite CFAPI 演示`r`n双击本文件，由 Windows 触发按需水合。`r`n" | Set-Content -LiteralPath $sample -Encoding utf8
}
Write-Host "模拟远端（原型运行中请勿修改）：$source"
Write-Host "请在资源管理器打开同步根：$syncRoot"
Write-Host '保持本窗口运行；Ctrl+C 断开提供程序。'
& $agent run --source $source --root $syncRoot
exit $LASTEXITCODE
