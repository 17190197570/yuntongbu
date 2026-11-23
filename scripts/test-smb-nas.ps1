[CmdletBinding()]
param(
    [Parameter(Mandatory)][Guid]$ConnectionId,
    [Parameter(Mandatory)][string]$RelativeFile,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedSha256,
    [int]$ReadTimeoutSeconds = 300,
    [switch]$WriteProbe
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$agent = Join-Path $projectRoot 'artifacts\bin\CloudDrive.Agent.dll'
$testArgs = @($agent,'smb-test',$ConnectionId.ToString())
if ($WriteProbe) { $testArgs += '--write-probe' }
& $dotnet @testArgs
if ($LASTEXITCODE -ne 0) { throw 'SMB 分阶段连接诊断未通过。未修改本地占位。' }
& $dotnet $agent smb-scan $ConnectionId.ToString()
if ($LASTEXITCODE -ne 0) { throw 'SMB 扫描未完成。检查点与旧索引保留。' }
# The C# harness reads credentials only through the current user's vault and saved configuration.
& $dotnet run --project (Join-Path $projectRoot 'tests\NasTests') -c Release --no-launch-profile -- $ConnectionId.ToString() $RelativeFile $ExpectedSha256 $ReadTimeoutSeconds
if ($LASTEXITCODE -ne 0) { throw 'NAS 水合测试失败；保留全部同步根与日志。' }
