using System.Text.Json;

namespace PulseForge.Contracts;

public static class IpcNames
{
    public const string Pipe = "PulseForge.AudioHost.v1";
    public const string TelemetryMap = "PulseForge.Telemetry.v1";
    public const int ProtocolVersion = 1;
}

public sealed record ControlRequestV1
{
    public int Version { get; init; } = IpcNames.ProtocolVersion;
    public required string Command { get; init; }
    public JsonElement Payload { get; init; } = JsonSerializer.SerializeToElement<object?>(null, ContractJson.Options);
}

public sealed record ControlResponseV1
{
    public int Version { get; init; } = IpcNames.ProtocolVersion;
    public bool Success { get; init; }
    public string? Error { get; init; }
    public JsonElement Payload { get; init; } = JsonSerializer.SerializeToElement<object?>(null, ContractJson.Options);
}

public sealed record StartBridgeRequestV1
{
    public required string SourceDeviceId { get; init; }
    public required string TargetDeviceId { get; init; }
    public OutputTransportV1 Transport { get; init; } = OutputTransportV1.RawShared;
    public BufferProfileV1 BufferProfile { get; init; } = BufferProfileV1.Balanced;
    public int PreferredSampleRate { get; init; } = 48_000;
}

public sealed record HandshakeResponseV1
{
    public int ProtocolVersion { get; init; } = IpcNames.ProtocolVersion;
    public int EngineAbiVersion { get; init; } = 1;
    public int OutputModeSchemaVersion { get; init; } = OutputModeV1.CurrentSchemaVersion;
    public int DeviceProfileSchemaVersion { get; init; } = DeviceProfileV1.CurrentSchemaVersion;
    public int TelemetrySchemaVersion { get; init; } = TelemetryFrameV1.CurrentSchemaVersion;
    public string HostVersion { get; init; } = "0.1.0-alpha";
}

public sealed record ParameterUpdateV1
{
    public required string Path { get; init; }
    public JsonElement Value { get; init; }
}

public sealed record SelectOutputRequestV1
{
    public required string TargetDeviceId { get; init; }
    public OutputTransportV1 Transport { get; init; } = OutputTransportV1.RawShared;
    public BufferProfileV1 BufferProfile { get; init; } = BufferProfileV1.Balanced;
    public int PreferredSampleRate { get; init; } = 48_000;
}
