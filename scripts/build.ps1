[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products Microsoft.VisualStudio.Product.BuildTools -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'MSVC C++ Build Tools are missing. Run scripts/bootstrap.ps1.' }
$cmakeDirectory = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin'
$ninjaDirectory = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja'
$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
$preset = "windows-$($Configuration.ToLowerInvariant())"
$originalLocation = Get-Location
try {
    Set-Location -LiteralPath $repositoryRoot
    $nativeCommand = "set `"PATH=$cmakeDirectory;$ninjaDirectory;%PATH%`" && call `"$vcvars`" && cmake --preset $preset && cmake --build --preset $preset"
    & cmd.exe /d /s /c $nativeCommand
    if ($LASTEXITCODE -ne 0) { throw "Native $Configuration build failed." }
    if (-not $NoRestore) {
        dotnet restore PulseForge.slnx
        if ($LASTEXITCODE -ne 0) { throw 'Managed dependency restore failed.' }
    }
    dotnet build PulseForge.slnx -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Managed $Configuration build failed." }
    $nativeDll = Join-Path $repositoryRoot "out\native\$preset\native\PulseForge.Engine.dll"
    foreach ($project in ('PulseForge.AudioHost', 'PulseForge.Editor', 'PulseForge.Engine.Client.Tests')) {
        $projectOutput = Join-Path $repositoryRoot "managed\$project\bin\$Configuration"
        $destination = Get-ChildItem -LiteralPath $projectOutput -Directory -Filter 'net10.0*' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $destination) { Copy-Item -LiteralPath $nativeDll -Destination $destination.FullName -Force }
    }
    $hostOutputRoot = Join-Path $repositoryRoot "managed\PulseForge.AudioHost\bin\$Configuration"
    $editorOutputRoot = Join-Path $repositoryRoot "managed\PulseForge.Editor\bin\$Configuration"
    $hostOutput = Get-ChildItem -LiteralPath $hostOutputRoot -Directory -Filter 'net10.0*' -ErrorAction SilentlyContinue | Select-Object -First 1
    $editorOutput = Get-ChildItem -LiteralPath $editorOutputRoot -Directory -Filter 'net10.0*' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $hostOutput -or $null -eq $editorOutput) { throw 'Managed editor or audio-host output is missing.' }
    $packagedHost = Join-Path $editorOutput.FullName 'AudioHost'
    New-Item -ItemType Directory -Path $packagedHost -Force | Out-Null
    Copy-Item -Path (Join-Path $hostOutput.FullName '*') -Destination $packagedHost -Recurse -Force
    Write-Output "PULSEFORGE_BUILD_OK configuration=$Configuration"
}
finally {
    Set-Location -LiteralPath $originalLocation
}
