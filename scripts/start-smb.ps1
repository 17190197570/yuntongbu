[CmdletBinding()]
param([switch]$Auto)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
if ($Auto) { & $dotnet (Join-Path $projectRoot 'artifacts\bin\CloudDrive.Agent.dll') smb-auto }
else { & $dotnet (Join-Path $projectRoot 'artifacts\bin\CloudDrive.Desktop.dll') --connections }
exit $LASTEXITCODE
