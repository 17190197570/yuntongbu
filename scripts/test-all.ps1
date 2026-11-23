[CmdletBinding()]
param([string]$ValidationDirectory = 'artifacts\validation\phase7-current')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$validation = if ([IO.Path]::IsPathRooted($ValidationDirectory)) { $ValidationDirectory } else { Join-Path $projectRoot $ValidationDirectory }
New-Item -ItemType Directory -Path $validation -Force | Out-Null
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$failures = [Collections.Generic.List[string]]::new()
$results = [Collections.Generic.List[string]]::new()
function Check([string]$Name, [scriptblock]$Action) {
    $global:LASTEXITCODE = 0
    try {
        & $Action 2>&1 | Tee-Object -FilePath (Join-Path $validation ($Name + '.log'))
        if ($LASTEXITCODE -ne 0) { throw "exit=$LASTEXITCODE" }
        Write-Host "PASS $Name"
        $results.Add("PASS $Name")
    }
    catch { $_ | Out-String | Tee-Object -FilePath (Join-Path $validation ($Name + '.log')) -Append; $failures.Add($Name); $results.Add("FAILED/BLOCKED $Name"); Write-Host "FAILED/BLOCKED $Name" }
}
Check 'build-release' { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release }
Check 'unit' { & (Join-Path $PSScriptRoot 'test-unit.ps1') }
Check 'connection-mappings' { & (Join-Path $PSScriptRoot 'test-connections.ps1') }
Check 'smb-native-host' { & (Join-Path $PSScriptRoot 'test-smb-host.ps1') }
Check 'phase2' { & (Join-Path $PSScriptRoot 'test-phase2.ps1') }
Check 'phase3-new-upload' { & (Join-Path $PSScriptRoot 'test-phase3.ps1') -Cfapi }
Check 'phase4-cache' { & (Join-Path $PSScriptRoot 'test-cache.ps1') -Cfapi }
Check 'phase5-archive' { & (Join-Path $PSScriptRoot 'test-archive.ps1') -Cfapi }
Check 'phase6-product' { & (Join-Path $PSScriptRoot 'test-product.ps1') }
Check 'phase7-health' { & (Join-Path $PSScriptRoot 'test-health.ps1') }
Check 'cfapi-local' { & (Join-Path $PSScriptRoot 'test-integration.ps1') }
Check 'cfapi-indexed' { & (Join-Path $PSScriptRoot 'test-indexed-cfapi.ps1') }
Check 'desktop-smoke' { & $dotnet (Join-Path $projectRoot 'artifacts\bin\CloudDrive.Desktop.dll') --smoke }
Check 'nas-harness-build' { & $dotnet build (Join-Path $projectRoot 'tests\NasTests') -c Release --nologo }
Check 'nas-new-upload-harness-build' { & $dotnet build (Join-Path $projectRoot 'tests\NewUploadNasTests') -c Release --nologo }
Check 'nas-cache-harness-build' { & $dotnet build (Join-Path $projectRoot 'tests\CacheNasTests') -c Release --nologo }
Check 'nas-archive-harness-build' { & $dotnet build (Join-Path $projectRoot 'tests\ArchiveNasTests') -c Release --nologo }
Check 'script-syntax' {
    foreach ($script in (Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1')) {
        $tokens = $null; $syntaxErrors = $null
        [void][Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$syntaxErrors)
        if ($syntaxErrors.Count) { throw ($syntaxErrors | Out-String) }
    }
    & python -m py_compile (Join-Path $projectRoot 'tests\IntegrationTests\test_cfapi.py')
}
$results | Tee-Object -FilePath (Join-Path $validation 'summary.txt')
"Actual NAS tests: NOT RUN by this suite. Use the explicit NAS harnesses separately; no saved user NAS is contacted automatically." | Tee-Object -FilePath (Join-Path $validation 'summary.txt') -Append
if ($failures.Count) {
    "FAILED/BLOCKED: $($failures -join ', ')" | Tee-Object -FilePath (Join-Path $validation 'summary.txt') -Append
    throw "部分自动化检查失败或被系统策略阻止。见 $validation。"
}
'PASS all available automated checks' | Tee-Object -FilePath (Join-Path $validation 'summary.txt') -Append
