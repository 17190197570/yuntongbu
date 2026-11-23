[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
[xml]$properties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$packageVersion = [string]$properties.Project.PropertyGroup.Version
if ($packageVersion -ne '0.7.1') { throw '此脚本只封存 0.7.1 连接与映射交付。' }
$delivery = Join-Path $projectRoot "artifacts\Connections-$packageVersion"
New-Item -ItemType Directory -Path $delivery -Force | Out-Null
$archive = Join-Path $delivery "CloudDriveLite-$packageVersion-Source.zip"
if (Test-Path -LiteralPath $archive) { throw '源码包已存在，拒绝覆盖封存内容。' }
$zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
try {
    $sources = @(foreach ($directory in @('src','tests','scripts','docs','installer')) {
        Get-ChildItem -LiteralPath (Join-Path $projectRoot $directory) -Recurse -File |
            Where-Object { [IO.Path]::GetRelativePath($projectRoot, $_.FullName) -notmatch '(^|\\)(bin|obj|__pycache__)(\\|$)|(^|\\)update-trust\.json$' }
    }) + @(Get-ChildItem -LiteralPath $projectRoot -File | Where-Object { $_.Extension -in @('.props','.targets','.sln','.slnx','.md','.json') -or $_.Name -in @('.gitignore','CMakeLists.txt') })
    foreach ($source in $sources) {
        $name = [IO.Path]::GetRelativePath($projectRoot, $source.FullName).Replace('\','/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $source.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $zip.Dispose() }
foreach ($name in @('CONNECTIONS.md','CONNECTIONS_VALIDATION.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot "docs\$name") -Destination $delivery }
foreach ($name in @('nas-connections-smoke.png','folder-mappings-smoke.png','desktop-smoke.txt')) { Copy-Item -LiteralPath (Join-Path $projectRoot "artifacts\bin\$name") -Destination $delivery }
Copy-Item -LiteralPath (Join-Path $projectRoot 'artifacts\validation\connections-0.7.1') -Destination (Join-Path $delivery 'validation') -Recurse
@'
# 0.7.1 连接与映射开发交付

Source.zip 为本次完整源码。界面、用法、版本迁移和限制见 CONNECTIONS.md；测试覆盖及失败见 CONNECTIONS_VALIDATION.md 与 validation。

未签名开发版本。58 项专项通过，完整回归仍有原第三阶段 0x8007018B；最终桌面 DLL 又被 Windows 应用控制拦截。两张截图及 desktop-smoke.txt 来自此前同一页面样式的成功烟测，不能替代最终桌面启动验收。未连接真实 NAS，不包含实际密码。

工作区 artifacts/bin 为最后一次 Release 构建。未制作或更新 MSI；历史 artifacts/Release（0.6.0）和 artifacts/Phase7（0.7.0）保持封存。本目录不是可安装的正式签名发行版。
'@ | Set-Content -LiteralPath (Join-Path $delivery 'DELIVERY.md') -Encoding utf8
Get-ChildItem -LiteralPath $delivery -Recurse -File | Where-Object Name -NE 'SHA256SUMS.txt' | Sort-Object FullName | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetRelativePath($delivery, $_.FullName).Replace('\','/')
} | Set-Content -LiteralPath (Join-Path $delivery 'SHA256SUMS.txt') -Encoding utf8
Write-Host "已封存：$archive；请同时阅读未验收和系统策略阻断记录。"
