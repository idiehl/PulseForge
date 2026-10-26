using PulseForge.Contracts;
using Xunit;

namespace PulseForge.Editor.Tests;

public sealed class FilterResponseTests
{
    [Fact]
    public void CalculatedResponse_IncludesTrimPolarityAndMicroDelayPhase()
    {
        var channel = ModeFactory.FlatChannel("T") with
        {
            ParametricFilters = [],
            InputTrimDb = 3,
            OutputTrimDb = -1,
            DelayMs = 0.25,
        };

        Assert.Equal(2, FilterResponse.CalculateDb(channel, 1_000, 48_000), 6);
        Assert.Equal(-90, FilterResponse.CalculatePhaseDegrees(channel, 1_000, 48_000), 6);
        Assert.Equal(90, FilterResponse.CalculatePhaseDegrees(channel with { PolarityInverted = true }, 1_000, 48_000), 6);
    }

    [Fact]
    public void CalculatedResponse_UsesCascadedButterworthPassStages()
    {
        var channel = ModeFactory.FlatChannel("T") with
        {
            ParametricFilters = [],
            HighPass = new PassFilterV1 { Enabled = true, FrequencyHz = 1_000, Slope = FilterSlopeV1.Db24 },
        };

        Assert.InRange(FilterResponse.CalculateDb(channel, 1_000, 48_000), -3.02, -3.00);
        Assert.True(FilterResponse.CalculateDb(channel, 100, 48_000) < -75);
        Assert.InRange(Math.Abs(FilterResponse.CalculatePhaseDegrees(channel, 500, 48_000)), 1, 180);
    }
}
