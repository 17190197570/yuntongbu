[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'public-source-files.ps1')
$publicFiles = @(Get-PublicSourceFiles -ProjectRoot $projectRoot | Sort-Object FullName)
if (-not $publicFiles.Count) { throw 'No public source files found.' }
$issues = [Collections.Generic.List[string]]::new()
# Report locations/categories only; never print matching secret text.
$checks = [ordered]@{
    'private key' = '-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----'
    'GitHub token' = '(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,})'
    'AWS access key' = '(?:AKIA|ASIA)[A-Z0-9]{16}'
    'literal credential assignment' = '(?i)\b(?:password|passwd|pwd|api[_-]?key|access[_-]?token)\s*[:=]\s*["''][^"''\r\n]{8,}["'']'
    'Windows user home path' = '(?i)C:[\\/]+Users[\\/]+(?!Public(?:[\\/]|\b)|Default(?:[\\/]|\b))[^\\/\s`"'']+'
}
foreach ($publicFile in $publicFiles) {
    $relative = [IO.Path]::GetRelativePath($projectRoot, $publicFile.FullName).Replace('\', '/')
    $lineNumber = 0
    foreach ($sourceLine in [IO.File]::ReadLines($publicFile.FullName)) {
        $lineNumber++
        foreach ($category in $checks.Keys) {
            if ([regex]::IsMatch($sourceLine, $checks[$category])) { $issues.Add("${relative}:${lineNumber}: $category") }
        }
    }
}
if (Test-Path -LiteralPath (Join-Path $projectRoot '.git')) {
    $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($publicFile in $publicFiles) { [void]$allowed.Add([IO.Path]::GetRelativePath($projectRoot, $publicFile.FullName).Replace('\', '/')) }
    $tracked = @(& git -c core.quotepath=false -C $projectRoot ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect Git source index.' }
    foreach ($trackedFile in $tracked) {
        if (-not $allowed.Contains($trackedFile)) { $issues.Add("Excluded file is tracked: $trackedFile") }
    }
}
if ($issues.Count) {
    $issues | ForEach-Object { Write-Host $_ }
    throw "Public-source review failed: $($issues.Count) possible sensitive/unreviewed entries."
}
Write-Host "PASS public-source review: $($publicFiles.Count) files; no matches in configured checks. This is a heuristic check, not a security audit."
