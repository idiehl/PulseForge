[CmdletBinding()]
param(
    [switch]$InstallMissing,
    [switch]$RequireWdk
)

$ErrorActionPreference = 'Stop'
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    if (-not $InstallMissing) { throw 'The .NET 10 SDK is missing. Re-run with -InstallMissing to install it.' }
    winget install --id Microsoft.DotNet.SDK.10 --exact --silent --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { throw 'The .NET SDK installation failed.' }
}
$dotnetVersion = dotnet --version
if (-not $dotnetVersion.StartsWith('10.', [StringComparison]::Ordinal)) { throw "PulseForge requires .NET SDK 10; found $dotnetVersion." }

if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer discovery is missing.' }
$installation = & $vswhere -latest -products Microsoft.VisualStudio.Product.BuildTools -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'MSVC Build Tools with the x64 C++ workload are required.' }

$cmake = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ninja = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
foreach ($requiredPath in ($cmake, $ninja, $vcvars)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) { throw "Required native build component is missing: $requiredPath" }
}

$wdkBuildRoot = 'C:\Program Files (x86)\Windows Kits\10\build'
$wdkProps = if (Test-Path -LiteralPath $wdkBuildRoot) {
    Get-ChildItem -LiteralPath $wdkBuildRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'WindowsDriver.Common.props') } |
        Sort-Object { [Version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'WindowsDriver.Common.props' } |
        Select-Object -First 1
}
$wdkState = if ($wdkProps) { "available:$((Split-Path -Parent $wdkProps | Split-Path -Leaf))" } else { 'not-installed' }
if ($RequireWdk -and -not $wdkProps) {
    throw 'The Windows Driver Kit is not installed. Normal application builds do not require it; driver work does.'
}

Write-Output "PULSEFORGE_BOOTSTRAP_OK dotnet=$dotnetVersion vs=$installation wdk=$wdkState"
