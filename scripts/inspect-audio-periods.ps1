[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
}

$executable = Join-Path $repositoryRoot "out\native\windows-$($Configuration.ToLowerInvariant())\native\PulseForge.PeriodDiagnostic.exe"
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "The period diagnostic is not built at $executable. Run without -NoBuild first."
}

& $executable
if ($LASTEXITCODE -ne 0) {
    throw "Endpoint period inspection failed with exit code $LASTEXITCODE."
}

Write-Output "PULSEFORGE_PERIOD_DIAGNOSTIC_OK configuration=$Configuration route-started=false audio-rendered=false"
