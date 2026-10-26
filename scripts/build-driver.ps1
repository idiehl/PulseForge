[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{40,64}$')]
    [string]$TestCertificateThumbprint,
    [switch]$TestEnvironment
)

$ErrorActionPreference = 'Stop'
if (-not $TestEnvironment) {
    throw 'Driver builds are excluded from normal builds. Re-run with -TestEnvironment only in an isolated driver-development environment.'
}
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$wdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$wdkVersion = Get-ChildItem -LiteralPath (Join-Path $wdkRoot 'Include') -Directory -ErrorAction SilentlyContinue |
    Where-Object {
        $parsed = $null
        [Version]::TryParse($_.Name, [ref]$parsed) -and
        $parsed.Build -ge 26100 -and
        (Test-Path -LiteralPath (Join-Path $_.FullName 'km'))
    } |
    Sort-Object { [Version]$_.Name } -Descending |
    Select-Object -First 1
if ($null -eq $wdkVersion) {
    throw 'Windows Driver Kit 10.0.26100 or newer is required. This script will not install it.'
}
$normalizedThumbprint = $TestCertificateThumbprint.ToUpperInvariant()
$signingCertificate = @(
    Get-ChildItem -LiteralPath 'Cert:\CurrentUser\My' -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath 'Cert:\LocalMachine\My' -ErrorAction SilentlyContinue
) | Where-Object { $_.Thumbprint -ceq $normalizedThumbprint -and $_.HasPrivateKey } | Select-Object -First 1
if ($null -eq $signingCertificate) {
    throw "The exact test certificate $normalizedThumbprint with a private key was not found in CurrentUser or LocalMachine My."
}
if ($signingCertificate.NotAfter -le (Get-Date)) { throw 'The selected PulseForge test certificate is expired.' }
$enhancedKeyUsageOids = @($signingCertificate.EnhancedKeyUsageList | ForEach-Object { [string]$_.ObjectId })
if ($enhancedKeyUsageOids -notcontains '1.3.6.1.5.5.7.3.3') {
    throw 'The selected PulseForge test certificate is not valid for code signing.'
}
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products Microsoft.VisualStudio.Product.BuildTools -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual Studio C++ Build Tools are required.' }
$msbuild = Join-Path $installation 'MSBuild\Current\Bin\amd64\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) { throw "MSBuild was not found at $msbuild." }

& (Join-Path $PSScriptRoot 'prepare-driver.ps1')
$commonProject = Join-Path $repositoryRoot 'driver\build\sysvad\EndpointsCommon\EndpointsCommon.vcxproj'
$project = Join-Path $repositoryRoot 'driver\build\sysvad\TabletAudioSample\TabletAudioSample.vcxproj'
& $msbuild $commonProject /m /t:Rebuild "/p:Configuration=$Configuration" /p:Platform=x64 /p:SignMode=Off /v:minimal
if ($LASTEXITCODE -ne 0) { throw "The pinned SysVAD EndpointsCommon $Configuration build failed." }
& $msbuild $project /m /t:Rebuild "/p:Configuration=$Configuration" /p:Platform=x64 /p:SignMode=Off /v:minimal
if ($LASTEXITCODE -ne 0) { throw "PulseForge virtual audio driver $Configuration build failed." }

$preparedRoot = Join-Path $repositoryRoot 'driver\build\sysvad'
$driverBinary = Get-ChildItem -LiteralPath $preparedRoot -Recurse -File -Filter 'tabletaudiosample.sys' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$driverInf = Get-ChildItem -LiteralPath $preparedRoot -Recurse -File -Filter 'ComponentizedAudioSample.inf' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($null -eq $driverBinary -or $null -eq $driverInf) { throw 'The WDK build did not produce the reviewed SYS and INF pair.' }

$packageRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "driver\packages\$Configuration"))
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'driver\packages'))
$allowedPrefix = $allowedRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $packageRoot.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing unexpected package path $packageRoot." }
if (Test-Path -LiteralPath $packageRoot) {
    $resolvedPackage = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $packageRoot))
    if (-not $resolvedPackage.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to remove unexpected path $resolvedPackage." }
    Remove-Item -LiteralPath $resolvedPackage -Recurse -Force
}
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Copy-Item -LiteralPath $driverBinary.FullName -Destination (Join-Path $packageRoot 'tabletaudiosample.sys') -Force
Copy-Item -LiteralPath $driverInf.FullName -Destination (Join-Path $packageRoot 'PulseForge.VirtualAudio.inf') -Force

$signTool = Get-ChildItem -LiteralPath (Join-Path $wdkRoot 'bin') -Recurse -File -Filter 'signtool.exe' |
    Where-Object FullName -Match '\\x64\\signtool\.exe$' |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $signTool) { throw 'SignTool.exe was not found in the installed WDK.' }
& $signTool.FullName sign /sha1 $TestCertificateThumbprint /fd SHA256 (Join-Path $packageRoot 'tabletaudiosample.sys')
if ($LASTEXITCODE -ne 0) { throw 'SignTool could not sign the PulseForge driver binary with the explicit test certificate.' }

$inf2Cat = Get-ChildItem -LiteralPath (Join-Path $wdkRoot 'bin') -Recurse -File -Filter 'Inf2Cat.exe' |
    Where-Object FullName -Match '\\(x64|x86)\\Inf2Cat\.exe$' |
    Sort-Object @{ Expression = { if ($_.FullName -match '\\x64\\') { 0 } else { 1 } } }, @{ Expression = 'FullName'; Descending = $true } |
    Select-Object -First 1
if ($null -eq $inf2Cat) { throw 'Inf2Cat.exe was not found in the installed WDK.' }
& $inf2Cat.FullName "/driver:$packageRoot" /os:10_X64
if ($LASTEXITCODE -ne 0) { throw 'Inf2Cat rejected the PulseForge driver package.' }
$catalog = Get-ChildItem -LiteralPath $packageRoot -File -Filter '*.cat' | Select-Object -First 1
if ($null -eq $catalog) { throw 'Inf2Cat did not create the PulseForge catalog.' }
& $signTool.FullName sign /sha1 $TestCertificateThumbprint /fd SHA256 $catalog.FullName
if ($LASTEXITCODE -ne 0) { throw 'SignTool could not sign the PulseForge catalog with the explicit test certificate.' }
$driverSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $packageRoot 'tabletaudiosample.sys')
$catalogSignature = Get-AuthenticodeSignature -LiteralPath $catalog.FullName
foreach ($signature in ($driverSignature, $catalogSignature)) {
    if ($signature.SignatureType -ne 'Authenticode' -or $null -eq $signature.SignerCertificate) {
        throw 'The generated package is missing an Authenticode signature.'
    }
    if ($signature.SignerCertificate.Thumbprint -cne $normalizedThumbprint) {
        throw 'The generated package signer does not match the exact test certificate.'
    }
    if ($signature.Status -notin ('Valid', 'UnknownError')) {
        throw "The generated package signature is not intact (status=$($signature.Status))."
    }
}
$publicCertificate = Join-Path $packageRoot 'PulseForge.DriverTest.cer'
Export-Certificate -Cert $signingCertificate -FilePath $publicCertificate -Force | Out-Null

$lock = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'driver\upstream.lock.json') | ConvertFrom-Json
$manifest = [ordered]@{
    schemaVersion = 1
    packageId = "PulseForge.VirtualAudio/$($lock.overlayVersion)"
    hardwareId = $lock.hardwareId
    endpointName = $lock.endpointName
    configuration = $Configuration
    upstreamCommit = $lock.commit
    signerThumbprint = $normalizedThumbprint
    signerSubject = $signingCertificate.Subject
    signerNotAfterUtc = $signingCertificate.NotAfter.ToUniversalTime().ToString('O')
    files = [ordered]@{
        'PulseForge.VirtualAudio.inf' = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $packageRoot 'PulseForge.VirtualAudio.inf')).Hash.ToLowerInvariant()
        'tabletaudiosample.sys' = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $packageRoot 'tabletaudiosample.sys')).Hash.ToLowerInvariant()
        $catalog.Name = (Get-FileHash -Algorithm SHA256 -LiteralPath $catalog.FullName).Hash.ToLowerInvariant()
        'PulseForge.DriverTest.cer' = (Get-FileHash -Algorithm SHA256 -LiteralPath $publicCertificate).Hash.ToLowerInvariant()
    }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $packageRoot 'package-manifest.json') -Encoding utf8NoBOM
Write-Output "PULSEFORGE_DRIVER_BUILD_OK package='$packageRoot' id='$($manifest.packageId)' signer='$($manifest.signerThumbprint)'"
