using PulseForge.Contracts;
using Xunit;

namespace PulseForge.Engine.Client.Tests;

public sealed class NativeEngineTests
{
    [Fact]
    public void AudioEnumeration_MapsStableAndHardwareIdentityWithoutStartingARoute()
    {
        using var engine = new NativeEngine();
        using var audio = new NativeAudioContext(engine);

        var devices = audio.RefreshDevices();

        Assert.NotEmpty(devices);
        Assert.All(devices, device => Assert.False(string.IsNullOrWhiteSpace(device.StableId)));
        var virtualEndpoint = devices.FirstOrDefault(device => device.IsPulseForgeVirtual);
        if (virtualEndpoint is not null)
        {
            Assert.Equal("Root\\PulseForgeVirtualAudio", virtualEndpoint.HardwareId, ignoreCase: true);
        }
    }

    [Fact]
    public void NativeAndManagedStructLayouts_Agree()
    {
        using var engine = new NativeEngine();
        var mode = ModeFactory.CreateEditable("Managed interop") with
        {
            MasterGainDb = 6,
            Limiter = new LimiterSettingsV1 { Enabled = true, CeilingDbTp = -1, ReleaseMs = 80 },
        };
        engine.ApplyMode(mode);
        var input = new float[2_048 * 2];
        for (var frame = 0; frame < input.Length / 2; frame++)
        {
            var sample = (float)(0.4 * Math.Sin(2 * Math.PI * 997 * frame / 48_000));
            input[frame * 2] = sample;
            input[frame * 2 + 1] = sample;
        }
        var output = new float[input.Length];
        engine.Process(input, output);
        Assert.All(output, value => Assert.True(float.IsFinite(value)));
        Assert.True(output.Max(Math.Abs) <= Math.Pow(10, -1.0 / 20.0) + 1e-5);
    }

    [Fact]
    public void Telemetry_ContainsWaveformSpectrumAndMeters()
    {
        using var engine = new NativeEngine();
        var input = new float[1_024 * 2];
        for (var frame = 0; frame < input.Length / 2; frame++)
        {
            input[frame * 2] = (float)(0.15 * Math.Sin(2 * Math.PI * 1_000 * frame / 48_000));
            input[frame * 2 + 1] = (float)(0.1 * Math.Sin(2 * Math.PI * 2_000 * frame / 48_000));
        }
        var output = new float[input.Length];
        engine.Process(input, output);
        var telemetry = engine.ReadTelemetry();
        Assert.Equal(TelemetryFrameV1.WaveformCapacity, telemetry.PostLeft.Length);
        Assert.Equal(TelemetryFrameV1.SpectrumCapacity, telemetry.PreSpectrumLeftDb.Length);
        Assert.Equal(TelemetryFrameV1.SpectrumCapacity, telemetry.SpectrumLeftDb.Length);
        Assert.True(telemetry.Sequence > 0);
        Assert.True(telemetry.PeakLeftDbFs > -30);
        Assert.Contains(telemetry.SpectrumLeftDb, value => value > -30);
        Assert.Contains(telemetry.PreSpectrumLeftDb, value => value > -30);
    }

    [Fact]
    public void IndependentRightChannelFilter_DoesNotLeakLeft()
    {
        using var engine = new NativeEngine();
        var rightFilters = ModeFactory.FlatChannel("R").ParametricFilters.ToArray();
        rightFilters[0] = rightFilters[0] with { FrequencyHz = 1_000, GainDb = 12, Q = 2, Enabled = true };
        var mode = ModeFactory.CreateEditable("Right only") with
        {
            Right = ModeFactory.FlatChannel("R") with { ParametricFilters = rightFilters },
            Left = ModeFactory.FlatChannel("L") with { ParametricFilters = [] },
            Limiter = new LimiterSettingsV1 { Enabled = false },
        };
        engine.ApplyMode(mode);
        var settle = new float[1_024 * 2];
        engine.Process(settle, settle);
        var input = new float[4_096 * 2];
        for (var frame = 0; frame < input.Length / 2; frame++)
        {
            var sample = (float)(0.02 * Math.Sin(2 * Math.PI * 1_000 * frame / 48_000));
            input[frame * 2] = sample;
            input[frame * 2 + 1] = sample;
        }
        var output = new float[input.Length];
        engine.Process(input, output);
        var leftPeak = output.Where((_, index) => index % 2 == 0).Max(Math.Abs);
        var rightPeak = output.Where((_, index) => index % 2 == 1).Max(Math.Abs);
        Assert.True(rightPeak > leftPeak * 2.5);
    }
}
