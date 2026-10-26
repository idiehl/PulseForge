# PulseForge alpha architecture

## Process and audio route

PulseForge is split into an Avalonia workstation and a per-user audio host. The
workstation is only a controller and telemetry consumer; closing it does not
stop a configured route.

```text
Windows shared-mode applications
  -> PulseForge Virtual Output (SysVAD-derived stereo render endpoint)
  -> fixed-capacity kernel render-to-loopback ring
  -> shared WASAPI loopback capture
  -> native stereo DSP graph
  -> bounded adaptive polyphase-sinc ring
  -> selected physical headphones or speakers
```

The source and target must be distinct. The host refuses a physical source, a
virtual target, or same-endpoint routing. Applications targeting a different
endpoint, protected content, and exclusive streams outside this route are not
claimed as processed.

Microsoft's stock SysVAD loopback pin returns a synthetic validation tone, not
the application render stream. The prepared PulseForge alpha.2 source replaces
that branch with a single-producer/single-consumer byte ring inside the driver,
suppresses the tone at source and in service configuration, and advertises a
driver safety revision. The host refuses to start a physical route unless the
installed revision is at least 2. The alpha ring format is deliberately fixed
at 48 kHz, stereo, signed 16-bit PCM and primes for five milliseconds before
consumption; underrun is zero-filled and unread data is never overwritten.

## Real-time boundary

The capture and render callbacks use only fixed-capacity native storage, atomic
state, bounded loops, and native DSP calls. They contain no managed calls,
filesystem access, UI access, logging, mutexes, waits, or dynamic allocation.
Mode changes are configured off the callback into an inactive pipeline. At the
next block boundary an atomic swap begins a 128-frame old/new path crossfade;
three preallocated slots allow the retiring path to remain immutable during the
crossfade.

The DSP operates on interleaved 32-bit float stereo samples with double-precision
coefficients and state. Telemetry is accumulated into fixed native frames and
published through lock-free slots. FFT and JSON/shared-memory publication occur
on the host analysis thread, never in the audio callback. The UI reads a bounded
double-slot memory map at no more than 60 Hz.

## WASAPI transport

The pinned miniaudio backend is extended by a reviewed patch for raw shared
playback. Its WASAPI path queries `IAudioClient3::GetSharedModeEnginePeriod`,
clamps the requested profile period to a supported fundamental/minimum/maximum,
and uses `InitializeSharedAudioStream` when the endpoint accepts it. PulseForge
then reports the negotiated internal period instead of assuming that 64 or 128
frames was accepted.

- Low Latency requests 64 frames.
- Balanced requests 128 frames and uses a two-period drift target.
- Safe requests 256 frames and uses a three-period drift target.

`PulseForge.PeriodDiagnostic` queries the same active render endpoints without
initializing or starting an audio stream. It reports shared-mode default,
fundamental, minimum, and maximum periods plus the device period exposed for
exclusive mode. Route state retains the requested period, negotiated virtual
source period, negotiated physical output period, and drift-buffer target as
separate values; the workstation presents the source/output pair alongside the
latency estimate.

The capture and playback clocks remain independent. A 128-tap, 512-phase
Blackman-windowed sinc interpolator applies a bounded ±0.2% fill correction.
The native diagnostic exercises the exact transport class at ±500 ppm.

## Failure and safety behavior

- A DSP processing error stops using the failed path and fades into unity input.
- Endpoint interruption marks the route failed and fades output to silence. No
  other physical endpoint is selected automatically.
- A newly started route ramps physical output from silence over 50 ms.
- The isolated driver diagnostic opens only the virtual render and loopback
  clients, verifies independent low-level stereo probes, and rejects the former
  3 kHz sample tone before any listening test is authorized.
- Positive master gain automatically arms the stereo-linked 4× detector,
  two-millisecond look-ahead limiter.
- Disabling that limiter while boosted requires an explicit expert dialog and
  leaves a persistent text-and-color clipping/hearing-risk indicator.
- PulseForge opens documented Windows Sound settings for activation. It never
  uses an undocumented API to change the default output.

## Persistence and compatibility

`OutputModeV1` contains portable DSP data only. `DeviceProfileV1` keeps local
endpoint identity, transport, buffer, channel mapping, format, and assigned mode
separately. Writes use a same-directory temporary file, last-good copy, and
atomic move. Imports are validated and renamed/re-identified instead of silently
overwriting an existing mode.

Windows endpoint enumeration retains the Core Audio endpoint ID and reads
`PKEY_Device_InstanceId`, falling back to `PKEY_Device_MatchingDeviceId`, on the
non-audio control thread. Recall resolves exact endpoint ID, then stable endpoint
ID, then hardware identity. The hardware tier is accepted only when exactly one
saved profile and one currently available output match, so a codec exposing
multiple render endpoints can never be selected by accident. Existing V1 files
without the optional hardware field remain valid and are enriched the next time
that device profile is saved. `PulseForge.DeviceIdentityDiagnostic` exercises
this native-to-managed identity boundary without initializing an audio stream.
Transport and buffer policy changes are persisted immediately, while a
successful route start replaces the stored supported format with the negotiated
format reported by the native bridge.

`DeviceProfileSession` queues complete read/merge/write transactions in order,
captures each device/mode/policy choice before asynchronous work, and publishes
the new in-memory snapshot only after the file write succeeds. Closing the
workstation waits for pending device and explicit mode saves. Startup awaits
device recall and mode application before reporting success. A read-only
`getMode` pipe command exposes the host's current DSP snapshot for verification.

The native C ABI, named-pipe protocol, shared telemetry contract, modes, and
device profiles are versioned independently. PulseGen has no repository
dependency: it can audition through PulseForge by rendering to the installed
PulseForge virtual endpoint.
