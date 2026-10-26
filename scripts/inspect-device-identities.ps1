[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration }
$preset = "windows-$($Configuration.ToLowerInvariant())"
$diagnostic = Join-Path $repositoryRoot "out\native\$preset\native\PulseForge.DeviceIdentityDiagnostic.exe"
if (-not (Test-Path -LiteralPath $diagnostic)) { throw "Device identity diagnostic is missing: $diagnostic" }
& $diagnostic
if ($LASTEXITCODE -ne 0) { throw 'Device identity diagnostic failed.' }
Write-Output "PULSEFORGE_DEVICE_IDENTITY_DIAGNOSTIC_OK configuration=$Configuration route-started=false audio-rendered=false"
