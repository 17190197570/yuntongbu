[CmdletBinding()]
param([switch]$Build)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if ($Build) { & (Join-Path $PSScriptRoot 'build.ps1') }
$env:PYTHONUTF8 = '1'
& python (Join-Path $projectRoot 'tests\IntegrationTests\test_cfapi.py')
exit $LASTEXITCODE
