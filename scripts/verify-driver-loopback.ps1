[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$TestEnvironment
)

$ErrorActionPreference = 'Stop'
if (-not $TestEnvironment) { throw 'Refusing hardware loopback diagnostics without -TestEnvironment.' }
if (Get-Process -Name 'PulseForge.AudioHost' -ErrorAction SilentlyContinue) {
    throw 'Stop the PulseForge Audio Host before running the isolated virtual-loopback diagnostic.'
}

$driverRevision = Get-ItemPropertyValue `
    -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\PulseForgeVirtualAudio\Parameters' `
    -Name PulseForgeLoopbackVersion `
    -ErrorAction SilentlyContinue
if ($driverRevision -lt 2) {
    throw 'The installed driver does not report PulseForgeLoopbackVersion 2 or newer.'
}

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$preset = "windows-$($Configuration.ToLowerInvariant())"
$diagnostic = Join-Path $repositoryRoot "out\native\$preset\native\PulseForge.VirtualLoopbackDiagnostic.exe"
if (-not (Test-Path -LiteralPath $diagnostic -PathType Leaf)) {
    throw "The virtual-loopback diagnostic is missing at $diagnostic. Build PulseForge first."
}

& $diagnostic
if ($LASTEXITCODE -ne 0) {
    throw 'The non-audible PulseForge virtual-loopback diagnostic failed. Physical routing remains unsafe.'
}
Write-Output 'PULSEFORGE_DRIVER_LOOPBACK_OK physical-output=not-opened audio-host=stopped'
