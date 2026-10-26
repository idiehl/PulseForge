[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [switch]$TestEnvironment,
    [ValidatePattern('^[0-9A-Fa-f]{40,64}$')]
    [string]$CertificateThumbprint,
    [ValidateRange(1, 730)]
    [int]$ValidDays = 365
)

$ErrorActionPreference = 'Stop'
if (-not $TestEnvironment) {
    throw 'Refusing to create a driver test certificate without -TestEnvironment.'
}
if (-not $IsWindows) { throw 'PulseForge driver certificates can only be created on Windows.' }

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$subject = "CN=PulseForge Driver Test ($env:COMPUTERNAME)"
$target = if ($CertificateThumbprint) { "certificate $($CertificateThumbprint.ToUpperInvariant())" } else { "$subject in the current-user certificate store" }
$action = if ($CertificateThumbprint) { 'Export its public certificate for the guarded driver build' } else { 'Create a non-exportable code-signing key and export its public certificate for the guarded driver build' }
if (-not $PSCmdlet.ShouldProcess($target, $action)) {
    return
}

$certificate = if ($CertificateThumbprint) {
    Get-Item -LiteralPath "Cert:\CurrentUser\My\$($CertificateThumbprint.ToUpperInvariant())" -ErrorAction Stop
} else {
    New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $subject `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddDays($ValidDays)
}

if (-not $certificate.HasPrivateKey) { throw 'The generated PulseForge certificate has no private key.' }
$codeSigningOid = '1.3.6.1.5.5.7.3.3'
$enhancedKeyUsageOids = @($certificate.EnhancedKeyUsageList | ForEach-Object { [string]$_.ObjectId })
if ($enhancedKeyUsageOids -notcontains $codeSigningOid) {
    throw 'The generated PulseForge certificate is missing the Code Signing enhanced key usage.'
}
if ($certificate.Subject -cne $subject) { throw "The selected certificate subject is not the scoped PulseForge test subject '$subject'." }

$artifactRoot = Join-Path $repositoryRoot 'out\driver-signing'
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$publicCertificatePath = Join-Path $artifactRoot "$($certificate.Thumbprint).cer"
Export-Certificate -Cert $certificate -FilePath $publicCertificatePath -Force | Out-Null

Write-Output "PULSEFORGE_TEST_CERT_OK thumbprint=$($certificate.Thumbprint) subject='$subject' expires=$($certificate.NotAfter.ToString('O')) private-key=non-exportable scope=current-user trust=unchanged test-mode=unchanged"
