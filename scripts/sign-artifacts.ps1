[CmdletBinding()]
param([Parameter(Mandatory)][string]$CertificateThumbprint, [Parameter(Mandatory)][uri]$TimestampUrl, [Parameter(Mandatory)][string[]]$Paths)
$ErrorActionPreference = 'Stop'
if ($TimestampUrl.Scheme -ne 'https') { throw '必须使用可信HTTPS时间戳服务。' }
$signingCertificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $CertificateThumbprint.Replace(' ',''))
if (-not $signingCertificate.HasPrivateKey -or $signingCertificate.NotAfter -lt [DateTime]::Now -or -not ($signingCertificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' })) { throw '需要当前用户有效的代码签名证书及私钥。不要传入私钥或PFX密码。' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$signTool = Get-ChildItem -Path (Join-Path $projectRoot '.tools\sdk\buildtools\bin\*\x64\signtool.exe') -ErrorAction SilentlyContinue | Select-Object -First 1
$signExecutable = if ($signTool) { $signTool.FullName } else { (Get-Command signtool.exe -ErrorAction Stop).Source }
foreach ($signPath in $Paths) {
    $signFile = (Resolve-Path -LiteralPath $signPath).Path
    if ([IO.Path]::GetExtension($signFile) -notin @('.exe','.dll','.msi')) { throw '仅允许签名明确指定的EXE/DLL/MSI。' }
    & $signExecutable sign /sha1 $signingCertificate.Thumbprint /fd SHA256 /tr $TimestampUrl.AbsoluteUri /td SHA256 $signFile
    if ($LASTEXITCODE -ne 0) { throw "签名失败：$signFile" }
    & $signExecutable verify /pa /all /v $signFile
    if ($LASTEXITCODE -ne 0) { throw "签名后独立验签失败：$signFile" }
}
