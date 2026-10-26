# Alpha acceptance status

## Automated and local verification

The following checks run without changing the Windows default endpoint or
installing a driver:

```powershell
./scripts/bootstrap.ps1
./scripts/build.ps1 -Configuration Debug
./scripts/verify.ps1 -Configuration Debug -NoBuild
./scripts/build.ps1 -Configuration Release
./scripts/verify.ps1 -Configuration Release -NoBuild
./scripts/package.ps1 -Configuration Release -NoBuild
```

The automated suites cover parameter validation, calculated magnitude and phase
behavior, 12/24/36/48 dB-per-octave pass filters, channel isolation, polarity, delay,
balance, width, mono fold-down, click-free pipeline swaps, linked damping,
guarded +18 dB boost, true-peak limiting, NaN/Inf sanitization, exact silence,
determinism, telemetry, and the exact adaptive resampler. Managed tests cover
native ABI layout, waveform/pre-post FFT/meters, atomic mode and device-profile
recovery, malformed import rejection, and six required viewport/scaling renders.

The package smoke check launches the published workstation, verifies that it
starts the published per-user host, closes the workstation, and verifies that
the host remains active. Test processes are then explicitly stopped.

### Packaged workstation and device recall: September 4, 2026

The updated Release package was staged and verified before replacement. Its
51-file manifest passed before promotion, after promotion, and after runtime
verification. Attempted promotion while the old editor and host were running
was refused as expected. The previous package was retained at
`out/package/PulseForge-alpha-win-x64.rollback-20260904T202253549Z-bfa42c4f`
and independently passed manifest verification. The running editor and host
executables, and their loaded application/native DLLs, matched the new package.

On the actual workstation, a Reference Flat duplicate was saved with the first
parametric band's gain set to -3.25 dB in both channels. The Realtek profile was
assigned that mode, Compatible shared transport, and Safe buffering. These
nondefault choices survived both an editor-only restart (the same host process
remained alive) and a full editor/host restart. UI inspection confirmed the
recalled controls; `getMode` confirmed the restarted host actually held the
same mode ID and both -3.25 dB values. The saved profile was byte-for-byte
unchanged through the restarts and retained its original profile ID while
adding the hardware identity.

The original Reference Flat / Raw shared / Balanced settings were restored,
and the verification-only custom mode and its backup were moved to
`out/verification/recall-20260904-131813`. The updated workstation was left open
with `ENGINE READY · ROUTE STOPPED`, demo off, no engine failure, and zero buffer
faults. Realtek remained the current Windows default. No physical audio stream
was started during this milestone.

This also fixed ordered device saves and awaited startup recall. Release
verification passed the native suite, 32 managed tests (including rapid-save
and failed-write recovery cases), six viewport/scaling renders, and the static
real-time callback audit. This verifies restart recall on the installed Realtek
device; a physical unplug/re-enumeration exercise remains separate.

## Driver and virtual-transport gate on this machine

Microsoft WDK 10.0.26100.6584, its VS 2022 Build Tools integration, and the
matching MSVC 14.44 Spectre libraries were installed on August 31, 2026. The
exact pinned SysVAD derivative was compiled as an x64 Universal driver, and the
Release package is available under `driver/packages/Release`.

Verified driver-package evidence:

- upstream commit `197ba2156a60e2b76fcd4820bae594223e91a1e9` and SysVAD tree
  `6fa502f5bfb3de1395a6c9ffe71e322fd9e28926`;
- package `PulseForge.VirtualAudio/1.0.0-alpha.2`, hardware ID
  `Root\PulseForgeVirtualAudio`, and endpoint name `PulseForge Virtual Output`;
- successful x64 Release compile, Inf2Cat signability check with zero warnings
  and errors, and Universal-mode InfVerif result `INF is VALID`;
- exact SHA-256 manifest match for the INF, SYS, catalog, and public test
  certificate;
- Authenticode signatures on the SYS and catalog from the exact manifest
  thumbprint `547E9D852B5AFD5A954A03D8AA70C069E1A33CA6`;
- full guarded installation validation through `install-driver.ps1 -WhatIf`;
- the exact public test certificate was imported into LocalMachine Root and
  TrustedPublisher, without importing its private key;
- the package was installed in place as the single `ROOT\MEDIA\0000` device;
  the device and its `Speakers (PulseForge Virtual Audio Device)` endpoint both
  report `CM_PROB_NONE`, and the service reports loopback revision 2;
- the installed driver-store binary matched the packaged binary's SHA-256 hash.

The certificate is intentionally self-signed. For this developer session the
user deliberately disabled Secure Boot, enabled Windows Test Mode, and rebooted;
Memory Integrity/HVCI remains running. PulseForge's scripts did not make those
boot-policy changes, disable BitLocker, choose the Windows default endpoint, or
reboot the machine. Windows selected the newly installed endpoint as default on
its own; guided activation continues to use documented Sound settings rather
than an undocumented default-device API.

### Corrected SysVAD loopback boundary

An initial physical-route probe exposed a critical limitation in the pinned
Microsoft sample: stock SysVAD simulates loopback with a loud 3 kHz sine wave
instead of the real application render mix. The bridge and audio host were
stopped immediately. Alpha.2 now copies the actual virtual render stream through
a bounded, non-blocking kernel ring, disables the sample tone at source and via
the service registry, and makes the host reject driver revisions below 2. A
50 ms startup fade is also applied before data reaches a physical output.

The revised device was cold-restarted and tested with the audio host stopped.
The guarded diagnostic opened only PulseForge's render and loopback clients; it
never opened Realtek, headphones, or speakers. Two consecutive Release runs
captured 192,000 frames each and passed independent-channel and obsolete-tone
checks:

- cold run: RMS -41.07 dBFS, left 997 Hz -33.97 dBFS, right 1603 Hz
  -36.47 dBFS, unexpected 3 kHz -96.79 dBFS;
- repeat run: RMS -41.07 dBFS, left 997 Hz -34.37 dBFS, right 1603 Hz
  -36.86 dBFS, unexpected 3 kHz -95.90 dBFS.

This proves the installed virtual render-to-loopback transport and left/right
separation without producing audible output. It does not yet prove the complete
virtual-to-physical listening route.

### Guarded physical-route probe

After explicit user confirmation, a bounded physical-route probe was run on
August 31, 2026. Before opening Realtek, its endpoint volume was measured at
100%, forced to 2%, and verified unmuted. PulseForge used Reference Flat at
0 dB master gain with the true-peak limiter armed, Raw shared transport, and the
Balanced buffer profile. The route negotiated 48 kHz, 480 frames, and 23.3 ms.

The four-second probe rendered low-level 997 Hz left and 1603 Hz right signals
through the PulseForge default endpoint while the host routed to Realtek. Its
simultaneous virtual-loopback capture passed with 192,000 frames, -41.07 dBFS
RMS, left -34.36 dBFS, right -36.86 dBFS, and the obsolete 3 kHz tone at
-96.24 dBFS. PulseForge reported zero underruns and zero overruns. The route was
then stopped and output returned to silence.

The first normal stop revealed a reporting bug: miniaudio's expected stopped
notification during deliberate device teardown was being recorded as an
endpoint failure. The callback now ignores teardown notifications after the
route's running flag is cleared. Release was rebuilt and reverified, and a
second start/stop cycle ended as `ENGINE READY · ROUTE STOPPED` with zero
underruns and overruns. Human confirmation that the low-level probe was actually
heard through the laptop speakers remains required before audible acceptance is
claimed.

The user did not hear the initial 2% probe. On September 4, 2026, the silent
driver gate passed again and the same bounded physical test was repeated after
raising only the Realtek endpoint from 2% to 4%. The repeat captured 192,000
frames at -41.07 dBFS RMS, measured the left 997 Hz probe at -34.36 dBFS and the
right 1603 Hz probe at -36.85 dBFS, and measured the obsolete 3 kHz tone at
-96.17 dBFS. The host again reported 48 kHz, 480 frames, 23.3 ms, and zero
underruns/overruns before returning cleanly to `ENGINE READY · ROUTE STOPPED`.
Human listening confirmation for this repeat remains pending.

At the user's request, a third bounded probe was run at a temporary Realtek
endpoint volume of 80%, then the endpoint was immediately restored and verified
at 4%. The route captured 192,000 frames at -41.07 dBFS RMS, measured the left
997 Hz probe at -34.79 dBFS and the right 1603 Hz probe at -37.29 dBFS, and kept
the obsolete 3 kHz component at -94.94 dBFS. PulseForge again reported zero
underruns/overruns and ended at `ENGINE READY · ROUTE STOPPED`. Human listening
confirmation for the 80% probe remains pending.

The user reported hearing only one indistinct tone for roughly one second. That
does not establish physical left/right separation because the original probe
played both frequencies simultaneously through closely spaced laptop speakers.
The diagnostic was therefore revised to an unambiguous six-second sequence:
1.5 seconds of 997 Hz on the left, 0.75 seconds of silence, then 1.5 seconds of
1603 Hz on the right, with 20 ms edge fades. The rebuilt sequential diagnostic
passed virtual-only validation with 288,000 captured frames, -43.09 dBFS RMS,
left -34.77 dBFS, right -34.37 dBFS, and the obsolete 3 kHz component at
-94.85 dBFS.

An explicitly confirmed physical replay of the revised sequential test was run
on September 4, 2026, with the Realtek endpoint temporarily set to 80%. It
captured 288,000 frames at -43.09 dBFS RMS, measured the isolated left 997 Hz
segment at -34.36 dBFS and the isolated right 1603 Hz segment at -34.75 dBFS,
and kept the obsolete 3 kHz component at -92.98 dBFS. The route reported 48 kHz,
480 frames, 23.3 ms, and zero underruns/overruns, then returned cleanly to
`ENGINE READY · ROUTE STOPPED`. The Realtek endpoint was immediately restored
and verified at 4% and unmuted. Human confirmation of hearing the left segment,
silence, and right segment in that order remains pending.

At the user's request, the same sequential test was repeated with the Realtek
endpoint temporarily set to 100%. It captured 288,000 frames at -43.09 dBFS
RMS, measured the isolated left 997 Hz segment at -34.79 dBFS and the isolated
right 1603 Hz segment at -33.98 dBFS, and kept the obsolete 3 kHz component at
-93.99 dBFS. PulseForge again reported 48 kHz, 480 frames, 23.3 ms, and zero
underruns/overruns before returning to `ENGINE READY · ROUTE STOPPED`. The
Realtek endpoint was immediately restored and verified at 4% and unmuted. The
user confirmed hearing both sequential tone segments. This establishes audible
output and left/right delivery through the complete virtual-to-Realtek route.

A new read-only endpoint-period diagnostic was added after audible acceptance.
It activates no stream, starts no route, and renders no audio. On this machine it
reported the following 48 kHz capabilities:

- Realtek shared and raw-shared: default, fundamental, minimum, and maximum are
  all 480 frames (10 ms); its device minimum for exclusive mode is 3 ms.
- PulseForge Virtual Output: 128-frame shared minimum and 96-frame raw-shared
  minimum, with a one-frame fundamental and 480-frame maximum.

This establishes that the validated 480-frame shared route is constrained by
the physical Realtek endpoint rather than PulseForge ignoring its 128-frame
Balanced request. Engine state and the workstation now report requested,
source, output, and drift-buffer frames independently. An exclusive-route
measurement is still required before claiming the under-10 ms Balanced target.

A separate read-only identity diagnostic enumerated both active endpoints with
no route and no rendered audio. Realtek exposed its stable Core Audio endpoint
ID plus `INTELAUDIO` matching-device identity, and PulseForge Virtual Output
exposed its stable endpoint ID plus `Root\PulseForgeVirtualAudio`. Managed tests
cover exact-ID priority, stable-ID recall, re-enumeration through a unique
hardware fallback, and refusal when either current outputs or saved profiles are
ambiguous. Device-profile JSON remains backward compatible because the hardware
identity is optional and is populated on the next save.
The workstation now persists transport and buffer choices immediately instead
of waiting for route activation, and records the negotiated format after a
successful start.

The following hardware acceptance items remain deliberately unclaimed:

- browser, media player, game, and PulseGen shared-mode route exercises;
- raw/shared/exclusive comparison on each intended physical endpoint;
- unplug/replug, endpoint loss, format-change, and host-restart listening tests;
- a 30-minute wall-clock underrun and physical clock-drift run;
- physical unplug/re-enumeration recall against the target hardware (editor and
  host restart recall on Realtek passed; hardware-fallback matching is covered
  by automated tests);
- the Balanced-profile target of less than 10 ms added latency. The validated
  shared physical route negotiated a 480-frame Realtek period and reported
  23.3 ms. Realtek advertises a 3 ms exclusive-mode minimum, but the exclusive
  route still requires a guarded measurement before the target can be claimed.

Before any new audible physical-route test, keep volume low, remove headphones,
and obtain explicit confirmation at the time of the test. The workstation's
**Activate** button opens Windows Sound settings for deliberate selection and
reports whether the endpoint is actually the current default.
