[CmdletBinding()]
param([string]$PackagePath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $PackagePath) { $PackagePath = Join-Path $projectRoot 'artifacts\Release\CloudDriveLite-x64.msi' }
$packagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$installerDatabase = New-Object -ComObject WindowsInstaller.Installer
$packageDatabase = $installerDatabase.OpenDatabase($packagePath,0)
function Scalar([string]$query) {
    $view = $packageDatabase.OpenView($query)
    try { [void]$view.Execute(); $record = $view.Fetch(); if ($record) { return $record.StringData(1) }; return $null }
    finally { [void]$view.Close() }
}
function AssertPackage([bool]$condition,[string]$name) { if (-not $condition) { throw "FAIL $name" }; Write-Host "PASS $name" }
$props = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)
AssertPackage ((Scalar "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductVersion'") -eq $props.Project.PropertyGroup.Version) 'MSI product version matches the single build version'
AssertPackage (-not (Scalar "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ALLUSERS'")) 'current-user MSI without machine-wide ALLUSERS'
AssertPackage ((Scalar "SELECT ``Directory_Parent`` FROM ``Directory`` WHERE ``Directory``='UserPrograms'") -eq 'LocalAppDataFolder') 'payload installed under current-user LocalAppData Programs'
AssertPackage ((Scalar "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='CheckSafeRemoval'") -eq 'product-uninstall-check') 'MSI invokes checked safe-removal helper'
AssertPackage ((Scalar "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='QuiesceUpgrade'") -eq 'product-upgrade-prepare') 'MSI quiesces processes before upgrade'
AssertPackage ((Scalar "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='FinishMaintenance'") -eq 'product-upgrade-finish') 'successful install checks database and finishes maintenance'
AssertPackage ((Scalar "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action``='CheckSafeRemoval'") -match 'NOT UPGRADINGPRODUCTCODE') 'upgrade removal preserves configured sync roots'
$fileView = $packageDatabase.OpenView('SELECT `FileName` FROM `File`')
$fileNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
try {
    [void]$fileView.Execute()
    while ($fileRecord = $fileView.Fetch()) { [void]$fileNames.Add(($fileRecord.StringData(1) -split '\|')[-1]) }
} finally { [void]$fileView.Close() }
AssertPackage ($fileNames.Contains('coreclr.dll')) 'self-contained .NET runtime included'
foreach ($binaryName in @('CloudDrive.Agent.exe','CloudDrive.Desktop.exe','CloudDrive.Bootstrap.exe','CloudProviderHost.exe')) {
    AssertPackage ($fileNames.Contains($binaryName)) "payload includes $binaryName"
}
$signature = Get-AuthenticodeSignature -LiteralPath $packagePath
Write-Host "MSI signature status: $($signature.Status) (developer package; a hash is not publisher authentication)."
Write-Host 'Static MSI checks only. No installation, upgrade, repair, rollback or removal was executed.'
