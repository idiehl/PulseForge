# PulseForge

PulseForge is a Windows 11 stereo output workstation and companion to PulseGen.
It combines a native, real-time-safe equalization engine with a compact Avalonia
control surface, independent left/right processing, guarded output boost,
portable modes, device recall, and live waveform/spectrum/phase telemetry.

**Developer alpha:** this repository contains the current workstation, host,
DSP engine, and virtual-driver source. System-wide processing requires the
separately built test driver and a deliberately configured Windows test
environment; there is no production-signed installer. See the
[alpha acceptance status](docs/ALPHA-ACCEPTANCE.md) for measured results and
remaining hardware checks, or [ProjectLab](https://atlasuniversalis.com/ProjectLab#pulseforge)
for the project overview.

## Verified alpha boundary

- Native C++20 stereo DSP with twelve parametric nodes per channel, cascaded
  high/low-pass filters, resonance damping, stereo utilities, micro-delay, and a
  linked look-ahead output limiter.
- C ABI used by deterministic offline tests and the independent audio host.
- Per-user host with a versioned named-pipe control protocol and shared-memory
  telemetry. When no PulseForge virtual endpoint is installed, it runs a clearly
  labeled visual demonstration signal without taking over or playing audio.
- Avalonia workstation following PulseGen's compact dark studio language.
- Versioned, atomically stored output modes and machine-local device profiles.
- WASAPI loopback-to-playback bridge for a distinct virtual source and physical
  target. Same-device routing is rejected to prevent duplication and feedback.
- A guarded SysVAD-derived virtual endpoint whose loopback pin carries the
  application render stream instead of the sample driver's synthetic test
  tone. Normal builds never install a driver, enable test signing, change the
  default output, or reboot.

The first alpha owns ordinary Windows shared-mode audio routed to the PulseForge
virtual endpoint. Applications that deliberately target another physical device,
exclusive streams, and protected content can bypass the shared route and are
reported as limitations rather than processed audio.

## Build and verify

Use Windows 11 x64, the .NET SDK 10.0.1xx feature band selected by
`global.json`, and a complete Visual Studio 2022 Build Tools installation with
the x64 C++ workload, CMake, and Ninja. `bootstrap.ps1` checks prerequisites;
normal application builds do not require the WDK.

```powershell
git clone https://github.com/idiehl/PulseForge.git
cd PulseForge
./scripts/bootstrap.ps1
./scripts/build.ps1 -Configuration Release
./scripts/verify.ps1 -Configuration Release
```

Run the workstation after a successful build:

```powershell
dotnet run --project managed/PulseForge.Editor/PulseForge.Editor.csproj -c Release
```

The editor launches the per-user audio host if it is not already running. The
host remains active when the editor closes.

Inspect the timing ranges advertised by active Windows render endpoints without
starting a route or rendering audio:

```powershell
./scripts/inspect-audio-periods.ps1 -Configuration Release -NoBuild
```

The report distinguishes the shared-mode default/fundamental/minimum/maximum
periods from the device's exclusive-mode minimum. Live engine state separately
reports the requested, virtual-source, physical-output, and drift-buffer frame
counts so a constrained physical endpoint is visible instead of being folded
into one ambiguous number.

Inspect the endpoint and hardware identities used for machine-local recall,
without opening an audio stream:

```powershell
./scripts/inspect-device-identities.ps1 -Configuration Release -NoBuild
```

Profiles match the exact Windows endpoint ID first, then its stable endpoint ID,
and finally a documented hardware property. Hardware fallback is used only when
it identifies exactly one saved profile and one currently available output;
ambiguous devices are left for deliberate user selection.
Transport and buffer changes are saved as soon as they are selected; a started
route also records the format that Windows actually negotiated.

Create a hash-manifested, framework-dependent Windows x64 alpha bundle (the
virtual driver is deliberately not bundled or installed):

```powershell
./scripts/package.ps1 -Configuration Release
```

When the workstation is running, stage the next package first:

```powershell
./scripts/package.ps1 -Configuration Release -StageOnly
```

After closing the workstation and its audio host, promote the exact staging
path printed by that command with `./scripts/package.ps1 -StagedPackagePath
'<staged path>'`. Publishing and hash validation finish before the existing
package is touched. Promotion refuses running packages and retains the previous
build in a timestamped rollback directory. Run `./scripts/verify-package.ps1`
to verify the current package's complete payload and engine hashes.

## Driver development

Driver work requires the Windows Driver Kit 10.0.26100 or newer and should be
performed in an isolated test environment first. See `driver/README.md`.
Nothing under `scripts/` enables Windows test-signing mode automatically.

After installing the reviewed alpha.2 driver, its virtual transport can be
checked without opening headphones or speakers:

```powershell
./scripts/verify-driver-loopback.ps1 -Configuration Release -TestEnvironment
```

The diagnostic refuses to run while the audio host is active, renders bounded
low-level test probes only into PulseForge Virtual Output, captures only that
endpoint's loopback stream, and fails if the stock SysVAD 3 kHz tone is present.
For unambiguous listening checks, the probe sequence is 1.5 seconds of 997 Hz
on the left, a 0.75-second silent gap, then 1.5 seconds of 1603 Hz on the right.

Implementation details and the exact verified/unverified alpha boundary are in
`docs/ARCHITECTURE.md` and `docs/ALPHA-ACCEPTANCE.md`.
