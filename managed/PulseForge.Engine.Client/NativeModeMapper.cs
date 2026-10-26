using System.Runtime.InteropServices;
using PulseForge.Contracts;

namespace PulseForge.Engine.Client;

internal static class NativeModeMapper
{
    internal static NativeConfig Map(OutputModeV1 mode, bool bypass = false)
    {
        ModeValidator.ThrowIfInvalid(mode);
        var config = new NativeConfig
        {
            Size = (uint)Marshal.SizeOf<NativeConfig>(),
            Version = 1,
            Left = Map(mode.Left),
            Right = Map(mode.Right),
            Damping = new NativeDampingConfig
            {
                Enabled = mode.Damping.Enabled ? 1u : 0u,
                StereoLinked = mode.Damping.StereoLinked ? 1u : 0u,
                SensitivityDb = mode.Damping.SensitivityDb,
                MinimumFrequencyHz = mode.Damping.MinimumFrequencyHz,
                MaximumFrequencyHz = mode.Damping.MaximumFrequencyHz,
                AttackMs = mode.Damping.AttackMs,
                ReleaseMs = mode.Damping.ReleaseMs,
                MaximumReductionDb = mode.Damping.MaximumReductionDb,
            },
            Stereo = new NativeStereoConfig
            {
                ChannelsLinked = mode.Stereo.ChannelsLinked ? 1u : 0u,
                Balance = mode.Stereo.Balance,
                WidthPercent = mode.Stereo.WidthPercent,
                MonoAudition = mode.Stereo.MonoAudition ? 1u : 0u,
            },
            Limiter = new NativeLimiterConfig
            {
                Enabled = mode.Limiter.Enabled ? 1u : 0u,
                ExpertBypassConfirmed = mode.Limiter.ExpertBypassConfirmed ? 1u : 0u,
                CeilingDbTp = mode.Limiter.CeilingDbTp,
                ReleaseMs = mode.Limiter.ReleaseMs,
                LookaheadMs = mode.Limiter.LookaheadMs,
            },
            MasterGainDb = mode.MasterGainDb,
            Bypass = bypass ? 1u : 0u,
        };
        return config;
    }

    private static NativeChannelConfig Map(ChannelChainV1 channel)
    {
        var filters = new NativeFilter[12];
        for (var index = 0; index < channel.ParametricFilters.Count && index < filters.Length; index++)
        {
            var source = channel.ParametricFilters[index];
            filters[index] = new NativeFilter
            {
                Enabled = source.Enabled ? 1u : 0u,
                Shape = (uint)source.Shape,
                FrequencyHz = source.FrequencyHz,
                GainDb = source.GainDb,
                Q = source.Q,
            };
        }
        return new NativeChannelConfig
        {
            InputTrimDb = channel.InputTrimDb,
            Mute = channel.Mute ? 1u : 0u,
            Solo = channel.Solo ? 1u : 0u,
            PolarityInverted = channel.PolarityInverted ? 1u : 0u,
            DelayMs = channel.DelayMs,
            HighPass = Map(channel.HighPass),
            FilterCount = (uint)channel.ParametricFilters.Count,
            Filters = filters,
            LowPass = Map(channel.LowPass),
            OutputTrimDb = channel.OutputTrimDb,
        };
    }

    private static NativePassFilter Map(PassFilterV1 filter) => new()
    {
        Enabled = filter.Enabled ? 1u : 0u,
        SlopeDbPerOctave = (uint)filter.Slope,
        FrequencyHz = filter.FrequencyHz,
    };
}
