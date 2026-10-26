using System.Text.Json.Serialization;

namespace PulseForge.Contracts;

public enum FilterShapeV1
{
    Bell,
    Notch,
    LowShelf,
    HighShelf,
}

public enum FilterSlopeV1
{
    Db12 = 12,
    Db24 = 24,
    Db36 = 36,
    Db48 = 48,
}

public sealed record ParametricFilterV1
{
    public required string Id { get; init; }
    public bool Enabled { get; init; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter<FilterShapeV1>))]
    public FilterShapeV1 Shape { get; init; } = FilterShapeV1.Bell;
    public double FrequencyHz { get; init; } = 1_000;
    public double GainDb { get; init; }
    public double Q { get; init; } = 1;
}

public sealed record PassFilterV1
{
    public bool Enabled { get; init; }
    public double FrequencyHz { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<FilterSlopeV1>))]
    public FilterSlopeV1 Slope { get; init; } = FilterSlopeV1.Db24;
}

public sealed record ChannelChainV1
{
    public double InputTrimDb { get; init; }
    public bool Mute { get; init; }
    public bool Solo { get; init; }
    public bool PolarityInverted { get; init; }
    public double DelayMs { get; init; }
    public PassFilterV1 HighPass { get; init; } = new() { FrequencyHz = 20 };
    public IReadOnlyList<ParametricFilterV1> ParametricFilters { get; init; } = [];
    public PassFilterV1 LowPass { get; init; } = new() { FrequencyHz = 20_000 };
    public double OutputTrimDb { get; init; }
}

public sealed record DampingSettingsV1
{
    public bool Enabled { get; init; }
    public bool StereoLinked { get; init; } = true;
    public double SensitivityDb { get; init; } = 5;
    public double MinimumFrequencyHz { get; init; } = 120;
    public double MaximumFrequencyHz { get; init; } = 16_000;
    public double AttackMs { get; init; } = 8;
    public double ReleaseMs { get; init; } = 180;
    public double MaximumReductionDb { get; init; } = 6;
}

public sealed record StereoSettingsV1
{
    public bool ChannelsLinked { get; init; } = true;
    public double Balance { get; init; }
    public double WidthPercent { get; init; } = 100;
    public bool MonoAudition { get; init; }
}

public sealed record LimiterSettingsV1
{
    public bool Enabled { get; init; } = true;
    public bool ExpertBypassConfirmed { get; init; }
    public double CeilingDbTp { get; init; } = -1;
    public double ReleaseMs { get; init; } = 80;
    public double LookaheadMs { get; init; } = 2;
}

public sealed record OutputModeV1
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public bool IsBuiltIn { get; init; }
    public ChannelChainV1 Left { get; init; } = ModeFactory.FlatChannel("L");
    public ChannelChainV1 Right { get; init; } = ModeFactory.FlatChannel("R");
    public DampingSettingsV1 Damping { get; init; } = new();
    public StereoSettingsV1 Stereo { get; init; } = new();
    public LimiterSettingsV1 Limiter { get; init; } = new();
    public double MasterGainDb { get; init; }
}

public static class ModeFactory
{
    private static readonly double[] Frequencies =
        [31.25, 62.5, 125, 250, 500, 1_000, 2_000, 4_000, 8_000, 16_000, 10_000, 80];

    public static ChannelChainV1 FlatChannel(string prefix)
    {
        return new ChannelChainV1
        {
            ParametricFilters = Frequencies.Select((frequency, index) => new ParametricFilterV1
            {
                Id = $"{prefix}{index + 1:00}",
                Enabled = index < 10,
                FrequencyHz = frequency,
                Q = index < 10 ? 1.1 : 0.8,
            }).ToArray(),
        };
    }

    public static OutputModeV1 CreateEditable(string name = "Untitled mode") => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Name = name,
    };
}
