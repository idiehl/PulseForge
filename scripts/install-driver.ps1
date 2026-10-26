[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string]$PackageDirectory,
    [Parameter(Mandatory)]
    [string]$ExactPackageId,
    [switch]$TestEnvironment
)

$ErrorActionPreference = 'Stop'
if (-not $TestEnvironment) { throw 'Refusing driver installation without -TestEnvironment.' }
$expectedPackageId = 'PulseForge.VirtualAudio/1.0.0-alpha.2'
if ($ExactPackageId -cne $expectedPackageId) { throw "ExactPackageId must be '$expectedPackageId'." }

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'driver\packages'))
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
$allowedPrefix = $allowedRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $packageRoot.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "PackageDirectory must resolve beneath $allowedRoot."
}
$manifestPath = Join-Path $packageRoot 'package-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'The exact PulseForge package manifest is missing.' }
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.packageId -cne $expectedPackageId -or $manifest.hardwareId -cne 'Root\PulseForgeVirtualAudio') {
    throw 'Package identity or hardware ID does not match the reviewed PulseForge alpha package.'
}

foreach ($property in $manifest.files.PSObject.Properties) {
    $filePath = Join-Path $packageRoot $property.Name
    if (-not (Test-Path -LiteralPath $filePath)) { throw "Package file is missing: $($property.Name)." }
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $filePath).Hash.ToLowerInvariant()
    if ($actualHash -cne [string]$property.Value) { throw "Hash validation failed for $($property.Name)." }
}
$infPath = Join-Path $packageRoot 'PulseForge.VirtualAudio.inf'
$inf = Get-Content -Raw -LiteralPath $infPath
if ($inf -notmatch 'Root\\PulseForgeVirtualAudio' -or $inf -notmatch 'PulseForge Virtual Output') {
    throw 'INF identity validation failed.'
}
$driverSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $packageRoot 'tabletaudiosample.sys')
$catalogPath = Get-ChildItem -LiteralPath $packageRoot -File -Filter '*.cat' | Select-Object -First 1
if ($null -eq $catalogPath) { throw 'The driver catalog is missing.' }
$catalogSignature = Get-AuthenticodeSignature -LiteralPath $catalogPath.FullName
foreach ($signature in ($driverSignature, $catalogSignature)) {
    if ($signature.SignatureType -ne 'Authenticode' -or $null -eq $signature.SignerCertificate) {
        throw 'Driver package is missing an Authenticode signature.'
    }
    if ($signature.Status -notin ('Valid', 'UnknownError')) {
        throw "Driver package signature is not intact (status=$($signature.Status))."
    }
}
if ($driverSignature.SignerCertificate.Thumbprint -cne $manifest.signerThumbprint -or $catalogSignature.SignerCertificate.Thumbprint -cne $manifest.signerThumbprint) {
    throw 'Driver and catalog signer thumbprints do not match the exact package manifest.'
}
$publicCertificatePath = Join-Path $packageRoot 'PulseForge.DriverTest.cer'
$publicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($publicCertificatePath)
if ($publicCertificate.Thumbprint -cne $manifest.signerThumbprint -or $publicCertificate.Subject -cne $manifest.signerSubject) {
    throw 'The packaged public certificate does not match the exact signer identity in the manifest.'
}

$wdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$devcon = Get-ChildItem -LiteralPath (Join-Path $wdkRoot 'Tools') -Recurse -File -Filter 'devcon.exe' |
    Where-Object FullName -Match '\\x64\\devcon\.exe$' |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $devcon) { throw 'WDK x64 DevCon was not found. This script will not install or download it.' }

$target = "$expectedPackageId at hardware ID Root\PulseForgeVirtualAudio"
if ($PSCmdlet.ShouldProcess($target, 'Install the exact test-signed virtual audio driver')) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'PulseForge driver installation requires an elevated PowerShell process.'
    }
    $existingDevice = Get-PnpDevice -Class MEDIA -ErrorAction SilentlyContinue | Where-Object {
        $hardwareIds = @(
            Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction SilentlyContinue
        ).Data
        @($hardwareIds) -contains 'Root\PulseForgeVirtualAudio'
    } | Select-Object -First 1
    $operation = if ($null -eq $existingDevice) { 'install' } else { 'update' }
    & $devcon.FullName $operation $infPath 'Root\PulseForgeVirtualAudio'
    $devconExitCode = $LASTEXITCODE
    if ($devconExitCode -notin (0, 1)) {
        throw "DevCon failed to install the exact PulseForge driver package (exit=$devconExitCode)."
    }
    $rebootRequired = $devconExitCode -eq 1
    Write-Output "PULSEFORGE_DRIVER_INSTALL_OK operation=$operation default-device=unchanged testsigning=unchanged reboot=not-requested reboot-required=$($rebootRequired.ToString().ToLowerInvariant())"
}
