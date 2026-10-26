# PulseForge virtual output driver

This directory defines a reproducible stereo virtual-output derivative of the
Microsoft SysVAD sample. The exact upstream commit and `audio/sysvad` tree are
pinned in `upstream.lock.json`. The overlay exposes one render endpoint intended
to be named **PulseForge Virtual Output** and removes SysVAD's microphone,
Bluetooth, HDMI, S/PDIF, keyword-detector, and sample-APO installation surface.

Stock SysVAD does not return the real render mix from its loopback pin; it emits
a synthetic 3 kHz validation tone. PulseForge alpha.2 replaces that path with a
fixed-capacity, non-blocking render-to-loopback ring, disables the sample tone at
source and through a service parameter, and marks the installed driver with
`PulseForgeLoopbackVersion=2`. The audio host refuses to route through an older
revision. The ring is intentionally fixed at stereo, 48 kHz, signed 16-bit PCM
for this alpha.

The generated source is intentionally not checked in. Prepare it from the
pinned Microsoft source with:

```powershell
./scripts/prepare-driver.ps1
```

This is a source preparation step only. It does not install software or change
Windows. A driver build is separate from the normal PulseForge build and
requires the WDK plus an explicit test-environment acknowledgement:

```powershell
./scripts/new-driver-test-certificate.ps1 `
  -TestEnvironment

./scripts/build-driver.ps1 `
  -Configuration Release `
  -TestCertificateThumbprint THUMBPRINT_FROM_THE_PREVIOUS_COMMAND `
  -TestEnvironment
```

The helper creates a current-user, non-exportable code-signing key and exports
only its public certificate beneath `out/driver-signing`. It does not trust the
certificate for kernel loading, enable test signing, or change boot policy. The
build validates the pinned upstream commit/tree, uses 64-bit MSBuild, signs the
SYS and catalog with the exact certificate, and writes a SHA-256 manifest.

Validate the complete install path without elevation or device changes:

```powershell
./scripts/install-driver.ps1 `
  -PackageDirectory ./driver/packages/Release `
  -ExactPackageId PulseForge.VirtualAudio/1.0.0-alpha.2 `
  -TestEnvironment `
  -WhatIf
```

Trusting the public test certificate is a separate, elevated action. The helper
validates the exact package and imports only the public certificate into the
local-machine Root and TrustedPublisher stores; it does not import the private
signing key:

```powershell
./scripts/trust-driver-test-certificate.ps1 `
  -PackageDirectory ./driver/packages/Release `
  -ExactPackageId PulseForge.VirtualAudio/1.0.0-alpha.2 `
  -TestEnvironment
```

Installation is a further elevated system step. It validates the exact
generated package, catalog, signatures, hashes, package identifier, and
hardware ID before invoking DevCon. If the exact root device already exists it
updates that device instead of creating a duplicate. It never enables test
signing, changes the default endpoint, disables Secure Boot or BitLocker, or
reboots:

```powershell
./scripts/install-driver.ps1 `
  -PackageDirectory ./driver/packages/Release `
  -ExactPackageId PulseForge.VirtualAudio/1.0.0-alpha.2 `
  -TestEnvironment
```

With the audio host stopped, verify the installed transport without opening a
physical output device:

```powershell
./scripts/verify-driver-loopback.ps1 `
  -Configuration Release `
  -TestEnvironment
```

This diagnostic sends sequential, low-level probes into the virtual render
endpoint—997 Hz on the left, silence, then 1603 Hz on the right—captures only
its loopback endpoint, and rejects a residual 3 kHz SysVAD sample tone. Passing
this check does not authorize or claim an audible headphones/speakers route.

Do the installation only on an isolated test machine after separately setting
up the WDK and a trusted test-signing environment. Production signing, HLK
qualification, and public distribution are outside this alpha.

The alpha route is ordinary shared-mode Windows audio. Applications choosing a
different physical endpoint, protected content, and physical exclusive streams
can bypass it. The workstation reports that boundary and never claims those
streams were processed.

The pinned upstream SysVAD source and this derivative retain the Microsoft Public
License (MS-PL); see the complete [upstream license](licenses/Microsoft.Windows-driver-samples.LICENSE.txt)
and `THIRD_PARTY_NOTICES.md`. Windows currently
shows the installed alpha endpoint as `Speakers (PulseForge Virtual Audio
Device)` on the development machine; changing that shell-generated display
label to the shorter intended name remains cosmetic follow-up work.
