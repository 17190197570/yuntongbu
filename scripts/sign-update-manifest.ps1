[CmdletBinding()]
param([Parameter(Mandatory)][string]$ManifestPath, [Parameter(Mandatory)][string]$CertificateThumbprint, [Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$manifestBytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $ManifestPath).Path)
if ($manifestBytes.Length -gt 65536) { throw '清单超出64KiB限制。' }
$manifestDocument = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
if (-not $manifestDocument.Version -or -not $manifestDocument.PackageSha256 -or -not $manifestDocument.ExpiresAt) { throw '缺少清单字段。发布前还必须通过客户端完整策略验证。' }
$manifestCertificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $CertificateThumbprint.Replace(' ',''))
$manifestKey = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($manifestCertificate)
if (-not $manifestKey -or $manifestKey.KeySize -lt 3072) { throw '清单签名需要RSA3072或更强私钥；私钥由Windows证书机制管理。' }
try {
    $manifestSignature = $manifestKey.SignData($manifestBytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    @{ Payload = [Convert]::ToBase64String($manifestBytes); Signature = [Convert]::ToBase64String($manifestSignature) } | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding utf8
} finally { $manifestKey.Dispose() }
