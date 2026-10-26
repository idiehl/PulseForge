using System.Text.Json;
using PulseForge.Contracts;
using Xunit;

namespace PulseForge.Contracts.Tests;

public sealed class IpcContractTests
{
    [Fact]
    public void EngineStateTiming_RoundTripsNegotiatedRouteDetails()
    {
        var state = new EngineStateV1
        {
            Status = "Processing system output",
            PeriodFrames = 480,
            RequestedPeriodFrames = 128,
            SourcePeriodFrames = 128,
            OutputPeriodFrames = 480,
            BufferedFrames = 960,
            Transport = OutputTransportV1.RawShared,
            BufferProfile = BufferProfileV1.Balanced,
            AddedLatencyMs = 23.3,
        };

        var json = JsonSerializer.Serialize(state, ContractJson.CompactOptions);
        var restored = JsonSerializer.Deserialize<EngineStateV1>(json, ContractJson.CompactOptions);

        Assert.NotNull(restored);
        Assert.Equal(128, restored.RequestedPeriodFrames);
        Assert.Equal(128, restored.SourcePeriodFrames);
        Assert.Equal(480, restored.OutputPeriodFrames);
        Assert.Equal(960, restored.BufferedFrames);
        Assert.Equal(OutputTransportV1.RawShared, restored.Transport);
        Assert.Equal(BufferProfileV1.Balanced, restored.BufferProfile);
        Assert.Contains("\"transport\":\"RawShared\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ParameterlessRequest_SerializesDefinedNullPayload()
    {
        var request = new ControlRequestV1 { Command = "handshake" };

        var json = JsonSerializer.Serialize(request, ContractJson.CompactOptions);
        var restored = JsonSerializer.Deserialize<ControlRequestV1>(json, ContractJson.CompactOptions);

        Assert.Contains("\"payload\":null", json, StringComparison.Ordinal);
        Assert.NotNull(restored);
        Assert.Equal(JsonValueKind.Null, restored.Payload.ValueKind);
    }

    [Fact]
    public void ErrorResponse_SerializesDefinedNullPayload()
    {
        var response = new ControlResponseV1 { Success = false, Error = "expected failure" };

        var json = JsonSerializer.Serialize(response, ContractJson.CompactOptions);
        var restored = JsonSerializer.Deserialize<ControlResponseV1>(json, ContractJson.CompactOptions);

        Assert.Contains("\"payload\":null", json, StringComparison.Ordinal);
        Assert.NotNull(restored);
        Assert.Equal(JsonValueKind.Null, restored.Payload.ValueKind);
    }
}
