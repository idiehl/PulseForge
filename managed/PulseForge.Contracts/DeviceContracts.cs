using System.Text.Json.Serialization;

namespace PulseForge.Contracts;

public enum OutputTransportV1
{
    RawShared,
    Exclusive,
    CompatibleShared,
}

public enum BufferProfileV1
{
    LowLatency,
    Balanced,
    Safe,
}

public enum SampleEncodingV1
{
    Float32,
}

public sealed record AudioFormatV1
{
    public int SampleRate { get; init; } = 48_000;
    public int Channels { get; init; } = 2;
    [JsonConverter(typeof(JsonStringEnumConverter<SampleEncodingV1>))]
    public SampleEncodingV1 Encoding { get; init; } = SampleEncodingV1.Float32;
}

public sealed record DeviceProfileV1
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Id { get; init; }
    public string? StableEndpointId { get; init; }
    public string? HardwareId { get; init; }
    public required string EndpointId { get; init; }
    public required string FriendlyName { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<OutputTransportV1>))]
    public OutputTransportV1 Transport { get; init; } = OutputTransportV1.RawShared;
    [JsonConverter(typeof(JsonStringEnumConverter<BufferProfileV1>))]
    public BufferProfileV1 BufferProfile { get; init; } = BufferProfileV1.Balanced;
    public int PreferredSampleRate { get; init; } = 48_000;
    public AudioFormatV1 SupportedFormat { get; init; } = new();
    public string ChannelMapping { get; init; } = "L,R";
    public string? AssignedModeId { get; init; }
}

public sealed record AudioDeviceV1
{
    public required string Id { get; init; }
    public string? StableId { get; init; }
    public string? HardwareId { get; init; }
    public required string Name { get; init; }
    public bool IsDefault { get; init; }
    public bool IsPulseForgeVirtual { get; init; }
}

public static class DeviceProfileResolver
{
    public static DeviceProfileV1? Resolve(
        AudioDeviceV1 device,
        IReadOnlyList<DeviceProfileV1> profiles,
        IReadOnlyList<AudioDeviceV1>? availableDevices = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(profiles);

        var exactEndpoint = FindUnique(profiles, profile => Same(profile.EndpointId, device.Id));
        if (exactEndpoint.Profile is not null) return exactEndpoint.Profile;
        if (exactEndpoint.Ambiguous) return null;

        if (!string.IsNullOrWhiteSpace(device.StableId))
        {
            var stableEndpoint = FindUnique(profiles, profile =>
                !string.IsNullOrWhiteSpace(profile.StableEndpointId) && Same(profile.StableEndpointId, device.StableId));
            if (stableEndpoint.Profile is not null) return stableEndpoint.Profile;
            if (stableEndpoint.Ambiguous) return null;
        }

        if (string.IsNullOrWhiteSpace(device.HardwareId)) return null;
        var hardwareProfile = FindUnique(profiles, profile =>
            !string.IsNullOrWhiteSpace(profile.HardwareId) && Same(profile.HardwareId, device.HardwareId));
        if (hardwareProfile.Profile is null) return null;

        if (availableDevices is not null)
        {
            var matchingDeviceCount = availableDevices.Count(candidate =>
                !string.IsNullOrWhiteSpace(candidate.HardwareId) && Same(candidate.HardwareId, device.HardwareId));
            if (matchingDeviceCount != 1) return null;
        }
        return hardwareProfile.Profile;
    }

    private static MatchResult FindUnique(
        IReadOnlyList<DeviceProfileV1> profiles,
        Func<DeviceProfileV1, bool> predicate)
    {
        DeviceProfileV1? match = null;
        foreach (var profile in profiles)
        {
            if (!predicate(profile)) continue;
            if (match is not null) return new MatchResult(null, true);
            match = profile;
        }
        return new MatchResult(match, false);
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private readonly record struct MatchResult(DeviceProfileV1? Profile, bool Ambiguous);
}

public sealed record EngineStateV1
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Status { get; init; } = "Offline";
    public string? SourceDeviceId { get; init; }
    public string? OutputDeviceId { get; init; }
    public int SampleRate { get; init; } = 48_000;
    public int PeriodFrames { get; init; } = 128;
    public int RequestedPeriodFrames { get; init; } = 128;
    public int SourcePeriodFrames { get; init; }
    public int OutputPeriodFrames { get; init; }
    public int BufferedFrames { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<OutputTransportV1>))]
    public OutputTransportV1 Transport { get; init; } = OutputTransportV1.RawShared;
    [JsonConverter(typeof(JsonStringEnumConverter<BufferProfileV1>))]
    public BufferProfileV1 BufferProfile { get; init; } = BufferProfileV1.Balanced;
    public AudioFormatV1 NegotiatedFormat { get; init; } = new();
    public double AddedLatencyMs { get; init; }
    public bool IsBypassed { get; init; }
    public bool IsDemoSignal { get; init; }
    public bool IsProcessing { get; init; }
    public bool TelemetryFrozen { get; init; }
    public ulong Underruns { get; init; }
    public ulong Overruns { get; init; }
    public string? Failure { get; init; }
    public IReadOnlyList<AudioDeviceV1> Devices { get; init; } = [];
}
