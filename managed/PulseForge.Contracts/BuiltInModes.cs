namespace PulseForge.Contracts;

public static class BuiltInModes
{
    public static IReadOnlyList<OutputModeV1> All { get; } =
    [
        Create("builtin.reference-flat", "Reference Flat", "Transparent starting point with guarded true-peak output."),
        Create("builtin.headphone-detail", "Headphone Detail", "A restrained clarity starting point; not a headphone calibration.",
            (4_000, 1.5, 0.9), (10_000, 1.2, 0.7), (125, -0.8, 1.0)),
        Create("builtin.speaker-warmth", "Speaker Warmth", "A gentle near-field speaker starting point; not a room measurement.",
            (90, 2.0, 0.75), (350, -1.2, 1.0), (8_000, -0.8, 0.75)),
        CreateNight(),
    ];

    private static OutputModeV1 Create(
        string id,
        string name,
        string description,
        params (double Frequency, double Gain, double Q)[] edits)
    {
        var mode = ModeFactory.CreateEditable(name);
        var left = Apply(mode.Left, edits, "L");
        var right = Apply(mode.Right, edits, "R");
        return mode with
        {
            Id = id,
            Name = name,
            Description = description,
            IsBuiltIn = true,
            Left = left,
            Right = right,
        };
    }

    private static OutputModeV1 CreateNight()
    {
        var baseMode = Create(
            "builtin.night-control",
            "Night Control",
            "Reduced extremes and guarded dynamics for low-level listening; not a hearing-safety guarantee.",
            (62.5, -3.0, 0.8), (8_000, -2.0, 0.8), (16_000, -3.5, 0.8));
        return baseMode with
        {
            Damping = new DampingSettingsV1 { Enabled = true, MaximumReductionDb = 4, SensitivityDb = 4 },
            Limiter = new LimiterSettingsV1 { Enabled = true, CeilingDbTp = -2, ReleaseMs = 120 },
            MasterGainDb = -2,
        };
    }

    private static ChannelChainV1 Apply(
        ChannelChainV1 channel,
        IReadOnlyList<(double Frequency, double Gain, double Q)> edits,
        string prefix)
    {
        var filters = channel.ParametricFilters.ToArray();
        for (var index = 0; index < edits.Count && index < filters.Length; index++)
        {
            filters[index] = filters[index] with
            {
                Id = $"{prefix}{index + 1:00}",
                Enabled = true,
                FrequencyHz = edits[index].Frequency,
                GainDb = edits[index].Gain,
                Q = edits[index].Q,
            };
        }

        return channel with { ParametricFilters = filters };
    }
}
