[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$DotNetPath,
    [string]$NativeCompiler,
    [string]$OutputDirectory = 'artifacts\bin'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

if (-not $DotNetPath) {
    $portableDotNet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $portableDotNet) { $DotNetPath = $portableDotNet }
    else { $DotNetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
if (-not $NativeCompiler) {
    $portableMsvc = Get-ChildItem -Path (Join-Path $projectRoot '.tools\msvc\VC\Tools\MSVC\*\bin\Hostx64\x64\cl.exe') -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($portableMsvc) { $NativeCompiler = $portableMsvc.FullName }
    elseif (Get-Command cl -ErrorAction SilentlyContinue) { $NativeCompiler = (Get-Command cl).Source }
    else { throw '需要 Visual Studio C++ Build Tools 和 Windows SDK；请在 x64 Native Tools PowerShell 中运行。' }
}

$nativeSource = Join-Path $projectRoot 'src\CloudProviderHost'
$sourceFiles = @(Get-ChildItem -LiteralPath $nativeSource -Filter '*.cpp' | ForEach-Object FullName)
if (-not $sourceFiles.Count) { throw 'CloudProviderHost C++ 源码缺失。' }
$nativeOutput = Join-Path $outputPath 'CloudProviderHost.exe'
$savedInclude, $savedLib, $savedPath = $env:INCLUDE, $env:LIB, $env:PATH
Push-Location $outputPath
try {
    if ([IO.Path]::GetFileName($NativeCompiler) -ieq 'cl.exe') {
        if ($NativeCompiler.StartsWith((Join-Path $projectRoot '.tools\msvc'), [StringComparison]::OrdinalIgnoreCase)) {
            $vcRoot = Split-Path (Split-Path (Split-Path (Split-Path $NativeCompiler -Parent) -Parent) -Parent) -Parent
            $sdkInclude = Get-ChildItem -Path (Join-Path $projectRoot '.tools\sdk\microsoft.windows.sdk.cpp\c\Include\*') -Directory | Sort-Object Name | Select-Object -Last 1
            $sdkLib = Join-Path $projectRoot '.tools\sdk\microsoft.windows.sdk.cpp.x64\c'
            $env:INCLUDE = @((Join-Path $vcRoot 'include'), (Join-Path $sdkInclude.FullName 'ucrt'), (Join-Path $sdkInclude.FullName 'shared'), (Join-Path $sdkInclude.FullName 'um'), (Join-Path $sdkInclude.FullName 'winrt')) -join ';'
            $env:LIB = @((Join-Path $vcRoot 'lib\x64'), (Join-Path $sdkLib 'ucrt\x64'), (Join-Path $sdkLib 'um\x64')) -join ';'
            $sdkTools = Get-ChildItem -Path (Join-Path $projectRoot '.tools\sdk\buildtools\bin\*\x64\mt.exe') -ErrorAction SilentlyContinue | Select-Object -First 1
            $env:PATH = (Split-Path $NativeCompiler -Parent) + ';' + $(if ($sdkTools) { $sdkTools.DirectoryName + ';' }) + $savedPath
        }
        $compilerArgs = @('/nologo', '/std:c++20', '/EHsc', '/W4', '/WX', '/utf-8', '/MT', '/DUNICODE', '/D_UNICODE', '/DNOMINMAX', '/DWIN32_LEAN_AND_MEAN', '/D_WIN32_WINNT=0x0A00', '/DNTDDI_VERSION=0x0A000008', "/Fe:$nativeOutput")
        if ($Configuration -eq 'Debug') { $compilerArgs += @('/Od', '/Z7') } else { $compilerArgs += '/O2' }
        $manifest = Join-Path $nativeSource 'CloudProviderHost.manifest'
        & $NativeCompiler @compilerArgs @sourceFiles /link CldApi.lib Bcrypt.lib Ole32.lib /MANIFEST:EMBED "/MANIFESTINPUT:$manifest"
    }
    else {
        throw '此构建脚本要求 MSVC cl.exe 和 Windows SDK。'
    }
    if ($LASTEXITCODE -ne 0) { throw "C++ 编译失败（$LASTEXITCODE）。" }
}
finally {
    $env:INCLUDE, $env:LIB, $env:PATH = $savedInclude, $savedLib, $savedPath
    Pop-Location
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
& $DotNetPath build (Join-Path $projectRoot 'src\CloudDrive.Agent\CloudDrive.Agent.csproj') -c $Configuration -o $outputPath --nologo
if ($LASTEXITCODE -ne 0) { throw ".NET 编译失败（$LASTEXITCODE）。" }
& $DotNetPath build (Join-Path $projectRoot 'src\CloudDrive.Desktop\CloudDrive.Desktop.csproj') -c $Configuration -o $outputPath --nologo
if ($LASTEXITCODE -ne 0) { throw "桌面界面编译失败（$LASTEXITCODE）。" }
Write-Host "构建完成：$outputPath"
& $DotNetPath build (Join-Path $projectRoot 'src\CloudDrive.Bootstrap\CloudDrive.Bootstrap.csproj') -c $Configuration -o $outputPath --nologo
if ($LASTEXITCODE -ne 0) { throw "Bootstrap 编译失败（$LASTEXITCODE）。" }
