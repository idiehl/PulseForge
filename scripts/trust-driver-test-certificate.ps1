[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string]$PackageDirectory,
    [Parameter(Mandatory)]
    [string]$ExactPackageId,
    [switch]$TestEnvironment
)

$ErrorActionPreference = 'Stop'
if (-not $TestEnvironment) { throw 'Refusing certificate trust changes without -TestEnvironment.' }
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
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'The exact PulseForge package manifest is missing.' }
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.packageId -cne $expectedPackageId -or $manifest.hardwareId -cne 'Root\PulseForgeVirtualAudio') {
    throw 'Package identity or hardware ID does not match the reviewed PulseForge alpha package.'
}

foreach ($property in $manifest.files.PSObject.Properties) {
    $filePath = Join-Path $packageRoot $property.Name
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) { throw "Package file is missing: $($property.Name)." }
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $filePath).Hash.ToLowerInvariant()
    if ($actualHash -cne [string]$property.Value) { throw "Hash validation failed for $($property.Name)." }
}

$publicCertificatePath = Join-Path $packageRoot 'PulseForge.DriverTest.cer'
$publicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($publicCertificatePath)
if ($publicCertificate.HasPrivateKey) { throw 'The packaged trust artifact unexpectedly contains a private key.' }
if ($publicCertificate.Thumbprint -cne $manifest.signerThumbprint -or $publicCertificate.Subject -cne $manifest.signerSubject) {
    throw 'The packaged public certificate does not match the exact signer identity in the manifest.'
}
if ($publicCertificate.Subject -cne $publicCertificate.Issuer) { throw 'The PulseForge test certificate must be self-signed.' }
$codeSigningOid = '1.3.6.1.5.5.7.3.3'
$enhancedKeyUsageOids = @($publicCertificate.EnhancedKeyUsageList | ForEach-Object { [string]$_.ObjectId })
if ($enhancedKeyUsageOids -notcontains $codeSigningOid) { throw 'The PulseForge test certificate is missing the Code Signing enhanced key usage.' }

$catalogPath = Join-Path $packageRoot 'pulseforge.virtualaudio.cat'
$driverPath = Join-Path $packageRoot 'tabletaudiosample.sys'
foreach ($signedPath in ($catalogPath, $driverPath)) {
    $signature = Get-AuthenticodeSignature -LiteralPath $signedPath
    if ($signature.SignatureType -ne 'Authenticode' -or $null -eq $signature.SignerCertificate) {
        throw "Package signature is missing: $signedPath"
    }
    if ($signature.Status -notin ('Valid', 'UnknownError')) {
        throw "Package signature is not intact: $signedPath (status=$($signature.Status))."
    }
    if ($signature.SignerCertificate.Thumbprint -cne $manifest.signerThumbprint) {
        throw "Package signer does not match the exact manifest: $signedPath"
    }
}

$target = "$($publicCertificate.Subject), thumbprint $($publicCertificate.Thumbprint)"
if ($PSCmdlet.ShouldProcess($target, 'Trust the exact PulseForge public test certificate for this local machine')) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Trusting the PulseForge driver test certificate requires an elevated PowerShell process.'
    }

    Import-Certificate -FilePath $publicCertificatePath -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    Import-Certificate -FilePath $publicCertificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null

    $rootPath = "Cert:\LocalMachine\Root\$($publicCertificate.Thumbprint)"
    $publisherPath = "Cert:\LocalMachine\TrustedPublisher\$($publicCertificate.Thumbprint)"
    if (-not (Test-Path -LiteralPath $rootPath) -or -not (Test-Path -LiteralPath $publisherPath)) {
        throw 'The PulseForge certificate was not found in both required local-machine trust stores after import.'
    }

    Write-Output "PULSEFORGE_TEST_CERT_TRUST_OK thumbprint=$($publicCertificate.Thumbprint) private-key=not-imported stores=LocalMachine/Root,LocalMachine/TrustedPublisher"
}
