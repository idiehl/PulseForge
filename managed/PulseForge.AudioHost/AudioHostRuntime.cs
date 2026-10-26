using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using PulseForge.Contracts;
using PulseForge.Engine.Client;

namespace PulseForge.AudioHost;

internal sealed class AudioHostRuntime : IAsyncDisposable
{
    private const int DemoBlockFrames = 256;
    private readonly NativeEngine engine;
    private readonly NativeAudioContext audio;
    private readonly SharedTelemetryWriter telemetryWriter;
    private readonly ModeLibrary modeLibrary;
    private readonly SemaphoreSlim controlGate = new(1, 1);
    private readonly SemaphoreSlim processingGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly float[] demoInput = new float[DemoBlockFrames * 2];
    private readonly float[] demoOutput = new float[DemoBlockFrames * 2];
    private IReadOnlyList<AudioDeviceV1> devices = [];
    private OutputModeV1 currentMode = BuiltInModes.All[0];
    private StartBridgeRequestV1? activeRoute;
    private string? selectedOutputId;
    private bool demoEnabled = true;
    private bool telemetryFrozen;
    private bool bypass;
    private long demoFrame;
    private bool disposed;

    private AudioHostRuntime(NativeEngine engine, NativeAudioContext audio, SharedTelemetryWriter telemetryWriter, ModeLibrary modeLibrary)
    {
        this.engine = engine;
        this.audio = audio;
        this.telemetryWriter = telemetryWriter;
        this.modeLibrary = modeLibrary;
    }

    internal static async Task<AudioHostRuntime> CreateAsync(CancellationToken cancellationToken)
    {
        var engine = new NativeEngine();
        try
        {
            var audio = new NativeAudioContext(engine);
            try
            {
                var runtime = new AudioHostRuntime(engine, audio, new SharedTelemetryWriter(), new ModeLibrary());
                var modes = await runtime.modeLibrary.LoadModesAsync(cancellationToken).ConfigureAwait(false);
                runtime.currentMode = modes.FirstOrDefault() ?? BuiltInModes.All[0];
                engine.ApplyMode(runtime.currentMode);
                runtime.devices = audio.RefreshDevices();
                return runtime;
            }
            catch
            {
                audio.Dispose();
                throw;
            }
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var demoTask = DemoLoopAsync(cancellationToken);
        var telemetryTask = TelemetryLoopAsync(cancellationToken);
        var pipeTask = PipeLoopAsync(cancellationToken);
        await Task.WhenAll(demoTask, telemetryTask, pipeTask).ConfigureAwait(false);
    }

    private async Task DemoLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(DemoBlockFrames * 1_000.0 / 48_000.0));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Volatile.Read(ref demoEnabled)) continue;
            await processingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!demoEnabled) continue;
                FillDemoSignal();
                engine.Process(demoInput, demoOutput);
            }
            finally
            {
                processingGate.Release();
            }
        }
    }

    private async Task TelemetryLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
        long lastSequence = -1;
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Volatile.Read(ref telemetryFrozen)) continue;
            try
            {
                var frame = engine.ReadTelemetry();
                if (frame.Sequence != lastSequence)
                {
                    telemetryWriter.Write(frame);
                    lastSequence = frame.Sequence;
                }
            }
            catch (PulseForgeNativeException exception) when (exception.ResultCode == (int)NativeResultCode.Busy)
            {
                // A bounded telemetry publication can be skipped when the producer swaps slots.
            }
        }
    }

    private async Task PipeLoopAsync(CancellationToken cancellationToken)
    {
        var clients = new HashSet<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(
                    IpcNames.Pipe,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    64 * 1024,
                    64 * 1024);
                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                var task = HandleClientAsync(pipe, cancellationToken);
                clients.Add(task);
                clients.RemoveWhere(client => client.IsCompleted);
            }
        }
        finally
        {
            await Task.WhenAll(clients).ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            ControlResponseV1 response;
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new IOException("Control request ended before a message was received.");
                if (line.Length > 1_048_576) throw new InvalidDataException("Control request exceeds the one-megabyte limit.");
                var request = JsonSerializer.Deserialize<ControlRequestV1>(line, ContractJson.CompactOptions)
                    ?? throw new InvalidDataException("Control request is empty.");
                if (request.Version != IpcNames.ProtocolVersion) throw new InvalidDataException($"Unsupported control protocol {request.Version}.");
                await controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                object result;
                try
                {
                    result = await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    controlGate.Release();
                }
                response = new ControlResponseV1
                {
                    Success = true,
                    Payload = JsonSerializer.SerializeToElement(result, ContractJson.Options),
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                response = new ControlResponseV1
                {
                    Success = false,
                    Error = exception.Message,
                };
            }
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, ContractJson.CompactOptions) + "\n");
            await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<object> ExecuteAsync(ControlRequestV1 request, CancellationToken cancellationToken)
    {
        switch (request.Command)
        {
            case "handshake":
                return new HandshakeResponseV1();
            case "getState":
                break;
            case "getMode":
                lock (stateGate) return currentMode;
            case "refreshDevices":
                if (audio.GetStatus().Running) throw new InvalidOperationException("Stop the active route before refreshing endpoints.");
                devices = audio.RefreshDevices();
                break;
            case "applyMode":
            {
                var mode = request.Payload.Deserialize<OutputModeV1>(ContractJson.Options)
                    ?? throw new InvalidDataException("An output mode is required.");
                ModeValidator.ThrowIfInvalid(mode);
                await ApplyEngineModeAsync(mode, bypass, cancellationToken).ConfigureAwait(false);
                lock (stateGate) currentMode = mode;
                break;
            }
            case "updateParameter":
            {
                var update = request.Payload.Deserialize<ParameterUpdateV1>(ContractJson.Options)
                    ?? throw new InvalidDataException("A parameter path and value are required.");
                OutputModeV1 mode;
                lock (stateGate) mode = ApplyParameterUpdate(currentMode, update);
                ModeValidator.ThrowIfInvalid(mode);
                await ApplyEngineModeAsync(mode, bypass, cancellationToken).ConfigureAwait(false);
                lock (stateGate) currentMode = mode;
                break;
            }
            case "selectOutput":
            {
                if (audio.GetStatus().Running) throw new InvalidOperationException("Stop the active route before selecting another output.");
                var selection = request.Payload.Deserialize<SelectOutputRequestV1>(ContractJson.Options)
                    ?? throw new InvalidDataException("A physical output selection is required.");
                if (selection.PreferredSampleRate is < 44_100 or > 192_000 || !Enum.IsDefined(selection.Transport) || !Enum.IsDefined(selection.BufferProfile))
                {
                    throw new InvalidDataException("The selected transport, buffer profile, or sample rate is invalid.");
                }
                var target = devices.FirstOrDefault(device => device.Id == selection.TargetDeviceId)
                    ?? throw new InvalidDataException("The selected output endpoint is no longer available.");
                if (target.IsPulseForgeVirtual) throw new InvalidDataException("The physical output cannot be the PulseForge virtual endpoint.");
                selectedOutputId = target.Id;
                break;
            }
            case "setBypass":
            {
                var requestedBypass = request.Payload.GetBoolean();
                OutputModeV1 mode;
                lock (stateGate) mode = currentMode;
                await ApplyEngineModeAsync(mode, requestedBypass, cancellationToken).ConfigureAwait(false);
                bypass = requestedBypass;
                break;
            }
            case "setDemo":
            {
                var enabled = request.Payload.GetBoolean();
                if (enabled && audio.GetStatus().Running) audio.StopBridge();
                demoEnabled = enabled;
                if (enabled) activeRoute = null;
                break;
            }
            case "startBridge":
            {
                var route = request.Payload.Deserialize<StartBridgeRequestV1>(ContractJson.Options)
                    ?? throw new InvalidDataException("A complete source and target route is required.");
                EnsureVirtualDriverSafetyRevision();
                await processingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    demoEnabled = false;
                    audio.StartBridge(route);
                    activeRoute = route;
                    selectedOutputId = route.TargetDeviceId;
                }
                catch
                {
                    audio.StopBridge();
                    demoEnabled = true;
                    throw;
                }
                finally
                {
                    processingGate.Release();
                }
                break;
            }
            case "stopBridge":
                audio.StopBridge();
                activeRoute = null;
                demoEnabled = false;
                break;
            case "freezeTelemetry":
                telemetryFrozen = request.Payload.GetBoolean();
                break;
            case "resetTelemetry":
            {
                engine.Reset();
                OutputModeV1 mode;
                lock (stateGate) mode = currentMode;
                await ApplyEngineModeAsync(mode, bypass, cancellationToken).ConfigureAwait(false);
                break;
            }
            default:
                throw new InvalidDataException($"Unknown control command '{request.Command}'.");
        }
        return CreateState();
    }

    private static void EnsureVirtualDriverSafetyRevision()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string parametersPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\PulseForgeVirtualAudio\Parameters";
        var revision = Registry.GetValue(parametersPath, "PulseForgeLoopbackVersion", null);
        if (revision is not int value || value < 2)
        {
            throw new InvalidOperationException(
                "The installed PulseForge virtual driver predates the real-loopback safety revision. " +
                "The physical route remains stopped; install PulseForge.VirtualAudio/1.0.0-alpha.2 or newer.");
        }
    }

    private async Task ApplyEngineModeAsync(OutputModeV1 mode, bool requestedBypass, CancellationToken cancellationToken)
    {
        await processingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PulseForgeNativeException? lastBusy = null;
            for (var attempt = 0; attempt < 32; attempt++)
            {
                try
                {
                    engine.ApplyMode(mode, requestedBypass);
                    lastBusy = null;
                    break;
                }
                catch (PulseForgeNativeException exception) when (exception.ResultCode == (int)NativeResultCode.Busy)
                {
                    lastBusy = exception;
                    if (!audio.GetStatus().Running) ProcessControlBlock();
                    else await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                }
            }
            if (lastBusy is not null) throw new InvalidOperationException("The audio callback did not accept a parameter snapshot within the bounded update window.", lastBusy);
            if (!audio.GetStatus().Running) ProcessControlBlock();
        }
        finally
        {
            processingGate.Release();
        }
    }

    private void ProcessControlBlock()
    {
        if (demoEnabled) FillDemoSignal();
        else Array.Clear(demoInput);
        engine.Process(demoInput, demoOutput);
    }

    private EngineStateV1 CreateState()
    {
        var bridge = audio.GetStatus();
        var isDemo = Volatile.Read(ref demoEnabled) && !bridge.Running;
        var status = bridge.Running ? "Processing system output" : isDemo ? "Visual demonstration · no audio takeover" : "Engine ready · route stopped";
        return new EngineStateV1
        {
            Status = status,
            SourceDeviceId = activeRoute?.SourceDeviceId,
            OutputDeviceId = activeRoute?.TargetDeviceId ?? selectedOutputId,
            SampleRate = bridge.SampleRate,
            PeriodFrames = bridge.PeriodFrames,
            RequestedPeriodFrames = bridge.RequestedPeriodFrames,
            SourcePeriodFrames = bridge.SourcePeriodFrames,
            OutputPeriodFrames = bridge.OutputPeriodFrames,
            BufferedFrames = bridge.BufferedFrames,
            Transport = bridge.Transport,
            BufferProfile = bridge.BufferProfile,
            AddedLatencyMs = bridge.AddedLatencyMs,
            IsBypassed = bypass,
            IsDemoSignal = isDemo,
            IsProcessing = bridge.Running || isDemo,
            TelemetryFrozen = telemetryFrozen,
            Underruns = bridge.Underruns,
            Overruns = bridge.Overruns,
            Failure = bridge.Failure,
            Devices = devices,
            NegotiatedFormat = new AudioFormatV1 { SampleRate = bridge.SampleRate, Channels = 2, Encoding = SampleEncodingV1.Float32 },
        };
    }

    private static OutputModeV1 ApplyParameterUpdate(OutputModeV1 mode, ParameterUpdateV1 update)
    {
        if (string.IsNullOrWhiteSpace(update.Path) || update.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new InvalidDataException("A non-empty parameter path and JSON value are required.");
        }
        var normalized = update.Path.Replace("[", ".", StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal);
        var path = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (path.Length < 2) throw new InvalidDataException($"Unsupported parameter path '{update.Path}'.");
        var root = path[0].ToLowerInvariant();
        var tail = path[1..];
        return root switch
        {
            "master" when tail.Length == 1 && tail[0].Equals("gainDb", StringComparison.OrdinalIgnoreCase) => UpdateMasterGain(mode, ReadDouble(update.Value)),
            "stereo" => mode with { Stereo = UpdateStereo(mode.Stereo, tail, update.Value) },
            "limiter" => mode with { Limiter = UpdateLimiter(mode.Limiter, tail, update.Value) },
            "damping" => mode with { Damping = UpdateDamping(mode.Damping, tail, update.Value) },
            "left" => mode with { Left = UpdateChannel(mode.Left, tail, update.Value) },
            "right" => mode with { Right = UpdateChannel(mode.Right, tail, update.Value) },
            _ => throw new InvalidDataException($"Unsupported parameter path '{update.Path}'."),
        };
    }

    private static OutputModeV1 UpdateMasterGain(OutputModeV1 mode, double gain)
    {
        var limiter = gain > 0 ? mode.Limiter with { Enabled = true, ExpertBypassConfirmed = false } : mode.Limiter;
        return mode with { MasterGainDb = gain, Limiter = limiter };
    }

    private static StereoSettingsV1 UpdateStereo(StereoSettingsV1 stereo, string[] path, JsonElement value)
    {
        if (path.Length != 1) throw new InvalidDataException("A stereo parameter name is required.");
        return path[0].ToLowerInvariant() switch
        {
            "channelslinked" => stereo with { ChannelsLinked = ReadBoolean(value) },
            "balance" => stereo with { Balance = ReadDouble(value) },
            "widthpercent" => stereo with { WidthPercent = ReadDouble(value) },
            "monoaudition" => stereo with { MonoAudition = ReadBoolean(value) },
            _ => throw new InvalidDataException($"Unsupported stereo parameter '{path[0]}'."),
        };
    }

    private static LimiterSettingsV1 UpdateLimiter(LimiterSettingsV1 limiter, string[] path, JsonElement value)
    {
        if (path.Length != 1) throw new InvalidDataException("A limiter parameter name is required.");
        return path[0].ToLowerInvariant() switch
        {
            "enabled" => limiter with { Enabled = ReadBoolean(value) },
            "expertbypassconfirmed" => limiter with { ExpertBypassConfirmed = ReadBoolean(value) },
            "ceilingdbtp" => limiter with { CeilingDbTp = ReadDouble(value) },
            "releasems" => limiter with { ReleaseMs = ReadDouble(value) },
            "lookaheadms" => limiter with { LookaheadMs = ReadDouble(value) },
            _ => throw new InvalidDataException($"Unsupported limiter parameter '{path[0]}'."),
        };
    }

    private static DampingSettingsV1 UpdateDamping(DampingSettingsV1 damping, string[] path, JsonElement value)
    {
        if (path.Length != 1) throw new InvalidDataException("A damping parameter name is required.");
        return path[0].ToLowerInvariant() switch
        {
            "enabled" => damping with { Enabled = ReadBoolean(value) },
            "stereolinked" => damping with { StereoLinked = ReadBoolean(value) },
            "sensitivitydb" => damping with { SensitivityDb = ReadDouble(value) },
            "minimumfrequencyhz" => damping with { MinimumFrequencyHz = ReadDouble(value) },
            "maximumfrequencyhz" => damping with { MaximumFrequencyHz = ReadDouble(value) },
            "attackms" => damping with { AttackMs = ReadDouble(value) },
            "releasems" => damping with { ReleaseMs = ReadDouble(value) },
            "maximumreductiondb" => damping with { MaximumReductionDb = ReadDouble(value) },
            _ => throw new InvalidDataException($"Unsupported damping parameter '{path[0]}'."),
        };
    }

    private static ChannelChainV1 UpdateChannel(ChannelChainV1 channel, string[] path, JsonElement value)
    {
        if (path.Length == 1)
        {
            return path[0].ToLowerInvariant() switch
            {
                "inputtrimdb" => channel with { InputTrimDb = ReadDouble(value) },
                "outputtrimdb" => channel with { OutputTrimDb = ReadDouble(value) },
                "mute" => channel with { Mute = ReadBoolean(value) },
                "solo" => channel with { Solo = ReadBoolean(value) },
                "polarityinverted" => channel with { PolarityInverted = ReadBoolean(value) },
                "delayms" => channel with { DelayMs = ReadDouble(value) },
                _ => throw new InvalidDataException($"Unsupported channel parameter '{path[0]}'."),
            };
        }
        if (path.Length == 2 && path[0].Equals("highPass", StringComparison.OrdinalIgnoreCase))
        {
            return channel with { HighPass = UpdatePassFilter(channel.HighPass, path[1], value) };
        }
        if (path.Length == 2 && path[0].Equals("lowPass", StringComparison.OrdinalIgnoreCase))
        {
            return channel with { LowPass = UpdatePassFilter(channel.LowPass, path[1], value) };
        }
        if (path.Length == 3 && path[0].Equals("filters", StringComparison.OrdinalIgnoreCase) && int.TryParse(path[1], out var index))
        {
            if (index < 0 || index >= channel.ParametricFilters.Count) throw new InvalidDataException("Filter index is out of range.");
            var filters = channel.ParametricFilters.ToArray();
            var filter = filters[index];
            filters[index] = path[2].ToLowerInvariant() switch
            {
                "enabled" => filter with { Enabled = ReadBoolean(value) },
                "shape" => filter with { Shape = ReadEnum<FilterShapeV1>(value) },
                "frequencyhz" => filter with { FrequencyHz = ReadDouble(value) },
                "gaindb" => filter with { GainDb = ReadDouble(value) },
                "q" => filter with { Q = ReadDouble(value) },
                _ => throw new InvalidDataException($"Unsupported filter parameter '{path[2]}'."),
            };
            return channel with { ParametricFilters = filters };
        }
        throw new InvalidDataException("Unsupported channel parameter path.");
    }

    private static PassFilterV1 UpdatePassFilter(PassFilterV1 filter, string name, JsonElement value) => name.ToLowerInvariant() switch
    {
        "enabled" => filter with { Enabled = ReadBoolean(value) },
        "frequencyhz" => filter with { FrequencyHz = ReadDouble(value) },
        "slope" => filter with { Slope = ReadEnum<FilterSlopeV1>(value) },
        _ => throw new InvalidDataException($"Unsupported pass-filter parameter '{name}'."),
    };

    private static double ReadDouble(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) || !double.IsFinite(result))
        {
            throw new InvalidDataException("The parameter requires a finite number.");
        }
        return result;
    }

    private static bool ReadBoolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean()
        : throw new InvalidDataException("The parameter requires a Boolean value.");

    private static T ReadEnum<T>(JsonElement value) where T : struct, Enum
    {
        if (value.ValueKind == JsonValueKind.String && Enum.TryParse<T>(value.GetString(), ignoreCase: true, out var named) && Enum.IsDefined(named)) return named;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric) && Enum.IsDefined(typeof(T), numeric)) return (T)Enum.ToObject(typeof(T), numeric);
        throw new InvalidDataException($"The parameter requires a valid {typeof(T).Name} value.");
    }

    private void FillDemoSignal()
    {
        for (var frame = 0; frame < DemoBlockFrames; frame++)
        {
            var absolute = demoFrame + frame;
            var time = absolute / 48_000.0;
            var sweep = 160 + 2_600 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 0.065 * time));
            var pulse = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 0.7 * time);
            demoInput[frame * 2] = (float)(0.16 * Math.Sin(2 * Math.PI * 73 * time) + 0.11 * Math.Sin(2 * Math.PI * sweep * time) + 0.045 * pulse * Math.Sin(2 * Math.PI * 6_200 * time));
            demoInput[frame * 2 + 1] = (float)(0.14 * Math.Sin(2 * Math.PI * 91 * time + 0.4) + 0.10 * Math.Sin(2 * Math.PI * (sweep * 1.13) * time + 0.9) + 0.04 * pulse * Math.Sin(2 * Math.PI * 8_100 * time));
        }
        demoFrame += DemoBlockFrames;
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        demoEnabled = false;
        audio.Dispose();
        engine.Dispose();
        telemetryWriter.Dispose();
        controlGate.Dispose();
        processingGate.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal enum NativeResultCode
{
    Busy = -5,
}
