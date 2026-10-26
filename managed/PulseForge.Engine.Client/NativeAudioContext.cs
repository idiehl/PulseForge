using System.Runtime.InteropServices;
using PulseForge.Contracts;

namespace PulseForge.Engine.Client;

public sealed class NativeAudioContext : IDisposable
{
    private readonly NativeEngine engine;
    private nint handle;
    private bool disposed;

    public NativeAudioContext(NativeEngine engine)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        NativeMethods.AudioContextCreate(out handle, engine.DangerousHandle).ThrowIfFailed("Create WASAPI context");
    }

    public IReadOnlyList<AudioDeviceV1> RefreshDevices()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        NativeMethods.AudioRefreshDevices(handle).ThrowIfFailed("Refresh audio endpoints");
        var count = NativeMethods.AudioGetDeviceCount(handle);
        var devices = new List<AudioDeviceV1>(checked((int)count));
        for (uint index = 0; index < count; index++)
        {
            var native = new NativeAudioDevice
            {
                Size = (uint)Marshal.SizeOf<NativeAudioDevice>(),
                Version = 1,
                Id = string.Empty,
                Name = string.Empty,
                StableId = string.Empty,
                HardwareId = string.Empty,
            };
            NativeMethods.AudioGetDevice(handle, index, ref native).ThrowIfFailed("Read audio endpoint");
            devices.Add(new AudioDeviceV1
            {
                Id = native.Id,
                StableId = string.IsNullOrWhiteSpace(native.StableId) ? native.Id : native.StableId,
                HardwareId = string.IsNullOrWhiteSpace(native.HardwareId) ? null : native.HardwareId,
                Name = native.Name,
                IsDefault = native.IsDefault != 0,
                IsPulseForgeVirtual = native.IsPulseForgeVirtual != 0,
            });
        }
        return devices;
    }

    public void StartBridge(StartBridgeRequestV1 request)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        var native = new NativeBridgeConfig
        {
            Size = (uint)Marshal.SizeOf<NativeBridgeConfig>(),
            Version = 1,
            Transport = (uint)request.Transport,
            BufferProfile = (uint)request.BufferProfile,
            PreferredSampleRate = checked((uint)request.PreferredSampleRate),
        };
        NativeMethods.AudioStartBridge(handle, request.SourceDeviceId, request.TargetDeviceId, in native).ThrowIfFailed("Start PulseForge audio route");
        try
        {
            engine.ApplyMode(engine.CurrentMode, engine.IsBypassed);
        }
        catch
        {
            NativeMethods.AudioStopBridge(handle);
            throw;
        }
    }

    public void StopBridge()
    {
        if (!disposed) NativeMethods.AudioStopBridge(handle);
    }

    public NativeBridgeSnapshot GetStatus()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var native = new NativeBridgeStatus
        {
            Size = (uint)Marshal.SizeOf<NativeBridgeStatus>(),
            Version = 1,
            Failure = string.Empty,
        };
        NativeMethods.AudioGetBridgeStatus(handle, ref native).ThrowIfFailed("Read audio route status");
        return new NativeBridgeSnapshot(
            native.Running != 0,
            checked((int)native.SampleRate),
            checked((int)native.PeriodFrames),
            checked((int)native.RequestedPeriodFrames),
            checked((int)native.SourcePeriodFrames),
            checked((int)native.OutputPeriodFrames),
            checked((int)native.BufferedFrames),
            (OutputTransportV1)native.Transport,
            (BufferProfileV1)native.BufferProfile,
            native.AddedLatencyMs,
            native.Underruns,
            native.Overruns,
            string.IsNullOrWhiteSpace(native.Failure) ? null : native.Failure);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (handle != nint.Zero)
        {
            NativeMethods.AudioContextDestroy(handle);
            handle = nint.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~NativeAudioContext() => Dispose();
}

public sealed record NativeBridgeSnapshot(
    bool Running,
    int SampleRate,
    int PeriodFrames,
    int RequestedPeriodFrames,
    int SourcePeriodFrames,
    int OutputPeriodFrames,
    int BufferedFrames,
    OutputTransportV1 Transport,
    BufferProfileV1 BufferProfile,
    double AddedLatencyMs,
    ulong Underruns,
    ulong Overruns,
    string? Failure);
