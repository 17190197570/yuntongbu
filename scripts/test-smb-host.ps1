[CmdletBinding()]
param([Guid]$StorageId = [Guid]::Empty)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$smbDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $smbDotnet)) { $smbDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$smbHostArgs = @('run', '--project', (Join-Path $projectRoot 'tests\SmbHostTests'), '-c', 'Release', '--no-launch-profile', '--')
if ($StorageId -ne [Guid]::Empty) { $smbHostArgs += @('--saved-storage', $StorageId.ToString()) }
& $smbDotnet @smbHostArgs
if ($LASTEXITCODE -ne 0) { throw '原生Windows PowerShell SMB宿主测试失败；未更改安全策略。默认不连接NAS。' }
