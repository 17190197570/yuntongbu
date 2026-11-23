[CmdletBinding()]
param([Parameter(Mandatory)][Guid]$ConnectionId)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$logs = Join-Path $projectRoot 'artifacts\validation\phase3'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
Push-Location $projectRoot
try {
    # Writes a unique TXT into the explicitly selected test NAS; keeps both copies and all journal evidence.
    & $dotnet run --project (Join-Path $projectRoot 'tests\NewUploadNasTests') -c Release --no-launch-profile -- $ConnectionId.ToString() 2>&1 | Tee-Object -FilePath (Join-Path $logs ('nas-new-upload-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log'))
    if ($LASTEXITCODE -ne 0) { throw '真实NAS新增上传未通过；测试项目保留。' }
}
finally { Pop-Location }
