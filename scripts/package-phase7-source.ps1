[CmdletBinding()]
param([string]$SampleReport)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$outputDirectory = Join-Path $projectRoot 'artifacts\Phase7'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$version = [string]$buildProperties.Project.PropertyGroup.Version
if ($version -ne '0.7.0') { throw '本脚本只封存第七阶段 0.7.0；0.7.1 请使用 package-connections-source.ps1。' }
$archive = Join-Path $outputDirectory ("CloudDriveLite-$version-Source.zip")
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
$zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
try {
    $files = @(foreach ($directory in @('src','tests','scripts','docs','installer')) {
        Get-ChildItem -LiteralPath (Join-Path $projectRoot $directory) -Recurse -File |
            Where-Object { [IO.Path]::GetRelativePath($projectRoot, $_.FullName) -notmatch '(^|\\)(bin|obj|__pycache__)(\\|$)|(^|\\)update-trust\.json$' }
    }) + @(Get-ChildItem -LiteralPath $projectRoot -File | Where-Object { $_.Extension -in @('.props','.targets','.sln','.slnx','.md','.json') -or $_.Name -in @('.gitignore','CMakeLists.txt') })
    foreach ($file in $files) {
        $name = [IO.Path]::GetRelativePath($projectRoot, $file.FullName).Replace('\','/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $zip.Dispose() }
foreach ($name in @('PHASE7.md','PHASE7_VALIDATION.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot "docs\$name") -Destination $outputDirectory }
$validationDirectory = Join-Path $outputDirectory 'validation'
New-Item -ItemType Directory -Path $validationDirectory -Force | Out-Null
$fullSuiteDirectory = Join-Path $validationDirectory 'full-suite'
New-Item -ItemType Directory -Path $fullSuiteDirectory -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'artifacts\validation\phase7-delivery') -File | Copy-Item -Destination $fullSuiteDirectory -Force
foreach ($name in @('health-safety.log','product-safety-lifecycle.log','code-integrity.json')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "artifacts\validation\phase7-final\$name") -Destination $validationDirectory
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'artifacts\bin\desktop-smoke.txt') -Destination $validationDirectory
$latestLifecycle = Join-Path $projectRoot 'artifacts\validation\phase7-delivery\product-health-lifecycle.log'
if (Test-Path -LiteralPath $latestLifecycle) { Copy-Item -LiteralPath $latestLifecycle -Destination $validationDirectory }
Copy-Item -LiteralPath (Join-Path $projectRoot 'artifacts\bin\health-center-smoke.png') -Destination $outputDirectory
if ($SampleReport) {
    $sample = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $SampleReport).Path)
    try { if ($sample.Entries.Count -ne 2 -or @($sample.Entries | Where-Object FullName -NotIn @('health.json','health.html')).Count) { throw '样例报告只允许 health.json/health.html。' } }
    finally { $sample.Dispose() }
    Copy-Item -LiteralPath $SampleReport -Destination (Join-Path $outputDirectory 'Sample-EmptyAgent-Health.zip')
}
@'
# 第七阶段开发交付

源码版本 0.7.0 / SQLite v5。Source.zip 含完整工程源码，排除工具链、生成目录和实际更新信任配置。请先读 PHASE7.md 和 PHASE7_VALIDATION.md。

health-center-smoke.png 是真实 Windows 桌面控件渲染，使用独立空测试库，未连接 NAS。Sample-EmptyAgent-Health.zip（若存在）来自真实空配置 Agent 的只读健康快照，仅 JSON/HTML，不是 NAS 成功报告。validation 保留完整回归、专项验证和应用控制证据，不能将旧成功结果替代最新失败。

本目录不含新 MSI；artifacts/Release 中第六阶段 0.6.0 开发 MSI 已单独封存，不能宣称包含第七阶段。尚无可信证书、更新站点及真实 NAS，正式发布和完整双向同步未验收。
'@ | Set-Content -LiteralPath (Join-Path $outputDirectory 'DELIVERY.md') -Encoding utf8
Get-ChildItem -LiteralPath $outputDirectory -File -Recurse | Where-Object Name -NE 'SHA256SUMS.txt' | Sort-Object FullName | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetRelativePath($outputDirectory,$_.FullName).Replace('\','/')
} | Set-Content -LiteralPath (Join-Path $outputDirectory 'SHA256SUMS.txt') -Encoding utf8
Write-Host "第七阶段源码交付：$archive；正式发布未验收。"
