namespace PulseForge.Contracts;

public sealed record TelemetryFrameV1
{
    public const int CurrentSchemaVersion = 1;
    public const int WaveformCapacity = 512;
    public const int SpectrumCapacity = 256;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public long Sequence { get; init; }
    public long MonotonicNanoseconds { get; init; }
    public int SampleRate { get; init; } = 48_000;
    public float[] PreLeft { get; init; } = new float[WaveformCapacity];
    public float[] PreRight { get; init; } = new float[WaveformCapacity];
    public float[] PostLeft { get; init; } = new float[WaveformCapacity];
    public float[] PostRight { get; init; } = new float[WaveformCapacity];
    public float[] PreSpectrumLeftDb { get; init; } = Enumerable.Repeat(-120f, SpectrumCapacity).ToArray();
    public float[] PreSpectrumRightDb { get; init; } = Enumerable.Repeat(-120f, SpectrumCapacity).ToArray();
    public float[] SpectrumLeftDb { get; init; } = Enumerable.Repeat(-120f, SpectrumCapacity).ToArray();
    public float[] SpectrumRightDb { get; init; } = Enumerable.Repeat(-120f, SpectrumCapacity).ToArray();
    public float PeakLeftDbFs { get; init; } = -120;
    public float PeakRightDbFs { get; init; } = -120;
    public float RmsLeftDbFs { get; init; } = -120;
    public float RmsRightDbFs { get; init; } = -120;
    public float TruePeakDbTp { get; init; } = -120;
    public float MomentaryLufs { get; init; } = -120;
    public float LimiterReductionDb { get; init; }
    public float Correlation { get; init; } = 1;
}
