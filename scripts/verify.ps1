[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration }
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products Microsoft.VisualStudio.Product.BuildTools -property installationPath
$ctest = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\ctest.exe'
$preset = "windows-$($Configuration.ToLowerInvariant())"
$originalLocation = Get-Location
try {
    Set-Location -LiteralPath $repositoryRoot
    & $ctest --preset $preset
    if ($LASTEXITCODE -ne 0) { throw 'Native tests failed.' }
    dotnet test PulseForge.slnx -c $Configuration --no-build --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) { throw 'Managed tests failed.' }
    & (Join-Path $PSScriptRoot 'check-realtime.ps1')
    git diff --check
    if ($LASTEXITCODE -ne 0) { throw 'Whitespace validation failed.' }
    Write-Output "PULSEFORGE_VERIFY_OK configuration=$Configuration native=passed managed=passed realtime-audit=passed whitespace=passed"
}
finally {
    Set-Location -LiteralPath $originalLocation
}
