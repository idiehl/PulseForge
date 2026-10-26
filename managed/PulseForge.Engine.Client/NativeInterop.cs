using System.Runtime.InteropServices;

namespace PulseForge.Engine.Client;

internal enum NativeResult
{
    Success = 0,
    InvalidArgument = -1,
    InvalidConfig = -2,
    OutOfMemory = -3,
    NotReady = -4,
    Busy = -5,
    AudioError = -6,
    NotFound = -7,
    Unsupported = -8,
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFilter
{
    public uint Enabled;
    public uint Shape;
    public double FrequencyHz;
    public double GainDb;
    public double Q;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePassFilter
{
    public uint Enabled;
    public uint SlopeDbPerOctave;
    public double FrequencyHz;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeChannelConfig
{
    public double InputTrimDb;
    public uint Mute;
    public uint Solo;
    public uint PolarityInverted;
    public double DelayMs;
    public NativePassFilter HighPass;
    public uint FilterCount;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)]
    public NativeFilter[] Filters;
    public NativePassFilter LowPass;
    public double OutputTrimDb;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDampingConfig
{
    public uint Enabled;
    public uint StereoLinked;
    public double SensitivityDb;
    public double MinimumFrequencyHz;
    public double MaximumFrequencyHz;
    public double AttackMs;
    public double ReleaseMs;
    public double MaximumReductionDb;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeStereoConfig
{
    public uint ChannelsLinked;
    public double Balance;
    public double WidthPercent;
    public uint MonoAudition;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeLimiterConfig
{
    public uint Enabled;
    public uint ExpertBypassConfirmed;
    public double CeilingDbTp;
    public double ReleaseMs;
    public double LookaheadMs;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeConfig
{
    public uint Size;
    public uint Version;
    public NativeChannelConfig Left;
    public NativeChannelConfig Right;
    public NativeDampingConfig Damping;
    public NativeStereoConfig Stereo;
    public NativeLimiterConfig Limiter;
    public double MasterGainDb;
    public uint Bypass;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTelemetry
{
    public uint Size;
    public uint Version;
    public ulong Sequence;
    public ulong MonotonicNanoseconds;
    public uint SampleRate;
    public uint FrameCount;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public float[] PreLeft;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public float[] PreRight;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public float[] PostLeft;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public float[] PostRight;
    public float PeakLeftDbFs;
    public float PeakRightDbFs;
    public float RmsLeftDbFs;
    public float RmsRightDbFs;
    public float TruePeakDbTp;
    public float MomentaryLufs;
    public float LimiterReductionDb;
    public float Correlation;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct NativeAudioDevice
{
    public uint Size;
    public uint Version;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 384)] public string Id;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
    public uint IsDefault;
    public uint IsPulseForgeVirtual;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 384)] public string StableId;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 384)] public string HardwareId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeBridgeConfig
{
    public uint Size;
    public uint Version;
    public uint Transport;
    public uint BufferProfile;
    public uint PreferredSampleRate;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct NativeBridgeStatus
{
    public uint Size;
    public uint Version;
    public uint Running;
    public uint SampleRate;
    public uint PeriodFrames;
    public double AddedLatencyMs;
    public ulong Underruns;
    public ulong Overruns;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Failure;
    public uint RequestedPeriodFrames;
    public uint SourcePeriodFrames;
    public uint OutputPeriodFrames;
    public uint BufferedFrames;
    public uint Transport;
    public uint BufferProfile;
}

internal static class NativeMethods
{
    private const string Library = "PulseForge.Engine";

    [DllImport(Library, EntryPoint = "pf_engine_create", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeResult EngineCreate(out nint engine);
    [DllImport(Library, EntryPoint = "pf_engine_destroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EngineDestroy(nint engine);
    [DllImport(Library, EntryPoint = "pf_engine_prepare", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeResult EnginePrepare(nint engine, uint sampleRate, uint maximumBlockFrames);
    [DllImport(Library, EntryPoint = "pf_engine_apply_config", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeResult EngineApplyConfig(nint engine, in NativeConfig config);
    [DllImport(Library, EntryPoint = "pf_engine_process", CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe NativeResult EngineProcess(nint engine, float* input, float* output, uint frameCount);
    [DllImport(Library, EntryPoint = "pf_engine_read_telemetry", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeResult EngineReadTelemetry(nint engine, ref NativeTelemetry telemetry);
    [DllImport(Library, EntryPoint = "pf_engine_reset", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EngineReset(nint engine);
    [DllImport(Library, EntryPoint = "pf_result_description", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint ResultDescription(NativeResult result);

    [DllImport(Library, EntryPoint = "pf_audio_context_create", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeResult AudioContextCreate(out nint context, nint engine);
    [DllImport(Library, EntryPoint = "pf_audio_context_destroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void AudioContextDestroy(nint context);
    [DllImport(Library, EntryPoint = "pf_audio_refresh_devices", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeResult AudioRefreshDevices(nint context);
    [DllImport(Library, EntryPoint = "pf_audio_get_device_count", CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint AudioGetDeviceCount(nint context);
    [DllImport(Library, EntryPoint = "pf_audio_get_device", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeResult AudioGetDevice(nint context, uint index, ref NativeAudioDevice device);
    [DllImport(Library, EntryPoint = "pf_audio_start_bridge", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern NativeResult AudioStartBridge(nint context, string sourceDeviceId, string targetDeviceId, in NativeBridgeConfig config);
    [DllImport(Library, EntryPoint = "pf_audio_stop_bridge", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void AudioStopBridge(nint context);
    [DllImport(Library, EntryPoint = "pf_audio_get_bridge_status", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeResult AudioGetBridgeStatus(nint context, ref NativeBridgeStatus status);
}

internal static class NativeResultExtensions
{
    internal static void ThrowIfFailed(this NativeResult result, string operation)
    {
        if (result == NativeResult.Success) return;
        var pointer = NativeMethods.ResultDescription(result);
        var description = pointer == nint.Zero ? result.ToString() : Marshal.PtrToStringUTF8(pointer) ?? result.ToString();
        throw new PulseForgeNativeException(operation, result, description);
    }
}

public sealed class PulseForgeNativeException
    : InvalidOperationException
{
    internal PulseForgeNativeException(string operation, NativeResult result, string description)
        : base($"{operation} failed: {description} ({(int)result}).")
    {
        Operation = operation;
        ResultCode = (int)result;
    }

    public string Operation { get; }
    public int ResultCode { get; }
}
