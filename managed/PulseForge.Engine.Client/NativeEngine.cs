using System.Runtime.InteropServices;
using PulseForge.Contracts;

namespace PulseForge.Engine.Client;

public sealed class NativeEngine : IDisposable
{
    private nint handle;
    private bool disposed;
    private OutputModeV1 currentMode = BuiltInModes.All[0];
    private bool bypass;

    public NativeEngine(int sampleRate = 48_000, int maximumBlockFrames = 8_192)
    {
        NativeMethods.EngineCreate(out handle).ThrowIfFailed("Create native engine");
        try
        {
            NativeMethods.EnginePrepare(handle, checked((uint)sampleRate), checked((uint)maximumBlockFrames)).ThrowIfFailed("Prepare native engine");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal nint DangerousHandle => handle;
    public OutputModeV1 CurrentMode => currentMode;
    public bool IsBypassed => bypass;

    public void ApplyMode(OutputModeV1 mode, bool? bypassOverride = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var requestedBypass = bypassOverride ?? bypass;
        var config = NativeModeMapper.Map(mode, requestedBypass);
        var result = NativeMethods.EngineApplyConfig(handle, in config);
        if (result == NativeResult.Busy)
        {
            Thread.Yield();
            result = NativeMethods.EngineApplyConfig(handle, in config);
        }
        result.ThrowIfFailed("Apply DSP mode");
        currentMode = mode;
        bypass = requestedBypass;
    }

    public void SetBypass(bool value) => ApplyMode(currentMode, value);

    public unsafe void Process(ReadOnlySpan<float> inputInterleaved, Span<float> outputInterleaved)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (inputInterleaved.Length == 0 || inputInterleaved.Length % 2 != 0 || outputInterleaved.Length < inputInterleaved.Length)
        {
            throw new ArgumentException("Stereo input must contain a non-empty, even sample count with matching output capacity.");
        }
        fixed (float* input = inputInterleaved)
        fixed (float* output = outputInterleaved)
        {
            NativeMethods.EngineProcess(handle, input, output, checked((uint)(inputInterleaved.Length / 2))).ThrowIfFailed("Process stereo block");
        }
    }

    public TelemetryFrameV1 ReadTelemetry()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var native = new NativeTelemetry
        {
            Size = (uint)Marshal.SizeOf<NativeTelemetry>(),
            Version = 1,
            PreLeft = new float[TelemetryFrameV1.WaveformCapacity],
            PreRight = new float[TelemetryFrameV1.WaveformCapacity],
            PostLeft = new float[TelemetryFrameV1.WaveformCapacity],
            PostRight = new float[TelemetryFrameV1.WaveformCapacity],
        };
        NativeMethods.EngineReadTelemetry(handle, ref native).ThrowIfFailed("Read engine telemetry");
        var (preSpectrumLeft, preSpectrumRight) = SpectrumAnalyzer.Calculate(native.PreLeft, native.PreRight);
        var (spectrumLeft, spectrumRight) = SpectrumAnalyzer.Calculate(native.PostLeft, native.PostRight);
        return new TelemetryFrameV1
        {
            Sequence = checked((long)native.Sequence),
            MonotonicNanoseconds = checked((long)native.MonotonicNanoseconds),
            SampleRate = checked((int)native.SampleRate),
            PreLeft = native.PreLeft,
            PreRight = native.PreRight,
            PostLeft = native.PostLeft,
            PostRight = native.PostRight,
            PreSpectrumLeftDb = preSpectrumLeft,
            PreSpectrumRightDb = preSpectrumRight,
            SpectrumLeftDb = spectrumLeft,
            SpectrumRightDb = spectrumRight,
            PeakLeftDbFs = native.PeakLeftDbFs,
            PeakRightDbFs = native.PeakRightDbFs,
            RmsLeftDbFs = native.RmsLeftDbFs,
            RmsRightDbFs = native.RmsRightDbFs,
            TruePeakDbTp = native.TruePeakDbTp,
            MomentaryLufs = native.MomentaryLufs,
            LimiterReductionDb = native.LimiterReductionDb,
            Correlation = native.Correlation,
        };
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        NativeMethods.EngineReset(handle);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (handle != nint.Zero)
        {
            NativeMethods.EngineDestroy(handle);
            handle = nint.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~NativeEngine() => Dispose();
}
