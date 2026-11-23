[CmdletBinding()]
param([switch]$SkipBuild, [string]$CertificateThumbprint, [uri]$TimestampUrl)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$packageDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $packageDotnet)) { $packageDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$wixPath = Join-Path $projectRoot '.tools\wix\wix.exe'
if (-not (Test-Path -LiteralPath $wixPath)) { throw '先将固定版本 WiX 6.0.2 安装到 .tools/wix（dotnet tool install wix --version 6.0.2 --tool-path .tools/wix）。' }
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release }
$packageProps = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)
$packageVersion = $packageProps.Project.PropertyGroup.Version
$releaseDirectory = Join-Path $projectRoot 'artifacts\Release'
$publishDirectory = Join-Path $projectRoot 'artifacts\publish\win-x64'
New-Item -ItemType Directory -Path $releaseDirectory,$publishDirectory -Force | Out-Null
foreach ($packageProject in @('CloudDrive.Agent','CloudDrive.Desktop','CloudDrive.Bootstrap')) {
    & $packageDotnet publish (Join-Path $projectRoot "src\$packageProject") -c Release -r win-x64 --self-contained true -o $publishDirectory --nologo -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { throw "发布 $packageProject 失败。" }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'artifacts\bin\CloudProviderHost.exe') -Destination $publishDirectory
$releaseTrust = Join-Path $projectRoot 'installer\update-trust.json'
$publishedTrust = Join-Path $publishDirectory 'Resources\update-trust.json'
if (-not (Test-Path -LiteralPath $releaseTrust) -and (Test-Path -LiteralPath $publishedTrust)) { Remove-Item -LiteralPath $publishedTrust }
if (Test-Path -LiteralPath $releaseTrust) {
    $releaseTrustConfig = Get-Content -LiteralPath $releaseTrust -Raw | ConvertFrom-Json
    if ($releaseTrustConfig.ManifestPublicKeyPem.Length -lt 100 -or $releaseTrustConfig.PublisherCertificateSha256.Length -ne 64 -or -not $releaseTrustConfig.AllowedHosts.Count) { throw '正式更新信任配置不完整。' }
    New-Item -ItemType Directory -Path (Join-Path $publishDirectory 'Resources') -Force | Out-Null
    Copy-Item -LiteralPath $releaseTrust -Destination (Join-Path $publishDirectory 'Resources\update-trust.json')
}
if ($CertificateThumbprint) {
    if (-not $TimestampUrl) { throw '代码签名需要可信时间戳URL。' }
    $ownedBinaries = @(Get-ChildItem -LiteralPath $publishDirectory -File | Where-Object { $_.Name -match '^CloudDrive\..*\.(exe|dll)$|^CloudProviderHost\.exe$' } | ForEach-Object FullName)
    & (Join-Path $PSScriptRoot 'sign-artifacts.ps1') -CertificateThumbprint $CertificateThumbprint -TimestampUrl $TimestampUrl -Paths $ownedBinaries
}
$payloadFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File | Where-Object Extension -notin @('.pdb','.png','.txt'))
function XmlEscape([string]$value) { return [Security.SecurityElement]::Escape($value) }
function StableId([string]$value) { return 'P' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($value.ToLowerInvariant()))).Substring(0,24) }
$directoryIds = @{ '.' = 'INSTALLFOLDER' }
$payloadXml = [Text.StringBuilder]::new()
[void]$payloadXml.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"><Fragment>')
$componentIds = [Collections.Generic.List[string]]::new()
foreach ($payloadFile in $payloadFiles) {
    $relative = [IO.Path]::GetRelativePath($publishDirectory,$payloadFile.FullName)
    $relativeDirectory = [IO.Path]::GetDirectoryName($relative)
    $current = '.'
    if ($relativeDirectory) {
        foreach ($part in $relativeDirectory.Split('\')) {
            $child = if ($current -eq '.') { $part } else { "$current\$part" }
            if (-not $directoryIds.ContainsKey($child)) {
                $directoryIds[$child] = StableId ('dir:' + $child)
                [void]$payloadXml.AppendLine('<DirectoryRef Id="' + $directoryIds[$current] + '"><Directory Id="' + $directoryIds[$child] + '" Name="' + (XmlEscape $part) + '" /></DirectoryRef>')
            }
            $current = $child
        }
    }
    $componentId = StableId ('component:' + $relative); $fileId = StableId ('file:' + $relative); $componentIds.Add($componentId)
    if ($relative -eq 'CloudDrive.Agent.exe') { $agentFileId = $fileId }
    [void]$payloadXml.AppendLine('<DirectoryRef Id="' + $directoryIds[$current] + '"><Component Id="' + $componentId + '" Guid="*"><File Id="' + $fileId + '" Source="' + (XmlEscape $payloadFile.FullName) + '" KeyPath="yes" /></Component></DirectoryRef>')
}
[void]$payloadXml.AppendLine('</Fragment><Fragment><ComponentGroup Id="Payload">')
foreach ($componentId in $componentIds) { [void]$payloadXml.AppendLine('<ComponentRef Id="' + $componentId + '" />') }
[void]$payloadXml.AppendLine('</ComponentGroup></Fragment></Wix>')
$payloadWxs = Join-Path $releaseDirectory 'Payload.wxs'; [IO.File]::WriteAllText($payloadWxs,$payloadXml.ToString())
$packageMsi = Join-Path $releaseDirectory 'CloudDriveLite-x64.msi'
$savedDotnetRoot = $env:DOTNET_ROOT
try {
    $env:DOTNET_ROOT = Split-Path $packageDotnet -Parent
    & $wixPath build (Join-Path $projectRoot 'installer\Package.wxs') $payloadWxs -arch x64 -d "ProductVersion=$packageVersion" -d "AgentFileId=$agentFileId" -o $packageMsi
    if ($LASTEXITCODE -ne 0) { throw 'WiX MSI构建失败。' }
} finally { $env:DOTNET_ROOT = $savedDotnetRoot }
if ($CertificateThumbprint) { & (Join-Path $PSScriptRoot 'sign-artifacts.ps1') -CertificateThumbprint $CertificateThumbprint -TimestampUrl $TimestampUrl -Paths @($packageMsi) }
$releaseNotes = @"
# CloudDrive Lite $packageVersion 开发包

日期：2026-11-20。请求发布者签名：$([bool]$CertificateThumbprint)。本次用户选择未签名开发交付，正式发布未验收。

包含当前用户 Bootstrap/Agent/Host 管理、托盘、八页 WinForms 界面、在线备份与保守恢复、脱敏诊断，以及需要可信发布配置的更新校验框架。

MSI 为 win-x64 当前用户安装，内含 .NET 10 和桌面运行时；无需单独运行环境 Bootstrapper。CloudDrive.Bootstrap.exe 为安装后的进程入口。

完整双向同步、真实 NAS、正式签名更新、干净 VM 安装修复升级卸载、长期运行尚未验收。第三阶段实际占位转换仍失败；已有文件保持只读。

非空根的卸载方案 A/B 尚未实现，存在连接/任务/受管根时阻止危险卸载。应取消卸载并保留程序及文件。

本机应用控制可能阻止未签名程序集；不得通过改名、复制加载或修改安全策略绕过。历史成功和最新阻断分别记录在测试报告中。

阅读 InstallGuide.pdf、RecoveryGuide.pdf、PHASE6_VALIDATION.md 和 PHASE6_COMPATIBILITY.md。MSI 静态检查通过不代表实际安装生命周期通过。

CloudDriveLite-Source.zip 包含 src、tests、scripts、docs、安装定义及根构建说明，排除工具链、生成物及真实更新信任配置。构建工具要求见 README.md。

SHA256SUMS.txt 仅用于字节完整性核对，不证明发布者身份。
"@
$releaseNotes | Set-Content -LiteralPath (Join-Path $releaseDirectory 'ReleaseNotes.md') -Encoding utf8
foreach ($releaseDocument in @('PHASE6.md','PHASE6_VALIDATION.md','PHASE6_COMPATIBILITY.md','INSTALL_GUIDE.md','RECOVERY_GUIDE.md')) {
    $releaseDocumentPath = Join-Path $projectRoot ('docs\' + $releaseDocument)
    if (Test-Path -LiteralPath $releaseDocumentPath) { Copy-Item -LiteralPath $releaseDocumentPath -Destination $releaseDirectory }
}
$sourceArchive = Join-Path $releaseDirectory 'CloudDriveLite-Source.zip'
if (Test-Path -LiteralPath $sourceArchive) { Remove-Item -LiteralPath $sourceArchive }
$sourceZip = [IO.Compression.ZipFile]::Open($sourceArchive,[IO.Compression.ZipArchiveMode]::Create)
try {
    $sourceFiles = @(foreach ($sourceDirectory in @('src','tests','scripts','docs','installer')) {
        Get-ChildItem -LiteralPath (Join-Path $projectRoot $sourceDirectory) -Recurse -File |
            Where-Object { [IO.Path]::GetRelativePath($projectRoot,$_.FullName) -notmatch '(^|\\)(bin|obj|__pycache__)(\\|$)|(^|\\)update-trust\.json$' }
    }) + @(Get-ChildItem -LiteralPath $projectRoot -File | Where-Object { $_.Extension -in @('.props','.targets','.sln','.slnx','.md','.json') -or $_.Name -in @('.gitignore','CMakeLists.txt') })
    foreach ($sourceFile in $sourceFiles) {
        $sourceEntry = [IO.Path]::GetRelativePath($projectRoot,$sourceFile.FullName).Replace('\','/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($sourceZip,$sourceFile.FullName,$sourceEntry,[IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $sourceZip.Dispose() }
Get-ChildItem -LiteralPath $releaseDirectory -File | Where-Object { $_.Name -in @('CloudDriveLite-x64.msi','CloudDriveLite-Source.zip','ReleaseNotes.md','InstallGuide.pdf','RecoveryGuide.pdf','PHASE6.md','PHASE6_VALIDATION.md','PHASE6_COMPATIBILITY.md','INSTALL_GUIDE.md','RECOVERY_GUIDE.md') } | Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  ' + $_.Name } | Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt')
Write-Host "开发MSI：$packageMsi（正式发布未验收）"
