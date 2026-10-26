using System.Numerics;
using PulseForge.Contracts;

namespace PulseForge.Editor;

internal static class FilterResponse
{
    internal static double CalculateDb(ChannelChainV1 channel, double frequency, double sampleRate)
    {
        var response = Calculate(channel, frequency, sampleRate);
        return 20 * Math.Log10(Math.Max(response.Magnitude, 1e-6));
    }

    internal static double CalculatePhaseDegrees(ChannelChainV1 channel, double frequency, double sampleRate)
    {
        var response = Calculate(channel, frequency, sampleRate);
        return Math.Atan2(response.Imaginary, response.Real) * 180 / Math.PI;
    }

    private static Complex Calculate(ChannelChainV1 channel, double frequency, double sampleRate)
    {
        var gain = Math.Pow(10, (channel.InputTrimDb + channel.OutputTrimDb) / 20);
        var response = new Complex(channel.PolarityInverted ? -gain : gain, 0);
        if (channel.DelayMs > 0)
        {
            response *= Complex.FromPolarCoordinates(1, -2 * Math.PI * frequency * channel.DelayMs / 1_000);
        }
        if (channel.HighPass.Enabled)
        {
            response *= PassResponse(channel.HighPass, frequency, sampleRate, highPass: true);
        }
        foreach (var filter in channel.ParametricFilters)
        {
            if (filter.Enabled) response *= Parametric(filter, frequency, sampleRate);
        }
        if (channel.LowPass.Enabled)
        {
            response *= PassResponse(channel.LowPass, frequency, sampleRate, highPass: false);
        }
        return response;
    }

    private static Complex PassResponse(PassFilterV1 filter, double frequency, double sampleRate, bool highPass)
    {
        var stageCount = (int)filter.Slope / 12;
        var order = stageCount * 2;
        var response = Complex.One;
        for (var stage = 0; stage < stageCount; stage++)
        {
            var angle = Math.PI * (2 * stage + 1) / (2 * order);
            var q = 1 / (2 * Math.Cos(angle));
            response *= PassStage(filter.FrequencyHz, q, frequency, sampleRate, highPass);
        }
        return response;
    }

    private static Complex PassStage(double cutoff, double q, double frequency, double sampleRate, bool highPass)
    {
        var omega = 2 * Math.PI * cutoff / sampleRate;
        var cosine = Math.Cos(omega);
        var alpha = Math.Sin(omega) / (2 * q);
        return highPass
            ? Evaluate((1 + cosine) * 0.5, -(1 + cosine), (1 + cosine) * 0.5, 1 + alpha, -2 * cosine, 1 - alpha, frequency, sampleRate)
            : Evaluate((1 - cosine) * 0.5, 1 - cosine, (1 - cosine) * 0.5, 1 + alpha, -2 * cosine, 1 - alpha, frequency, sampleRate);
    }

    private static Complex Parametric(ParametricFilterV1 filter, double frequency, double sampleRate)
    {
        var omega = 2 * Math.PI * filter.FrequencyHz / sampleRate;
        var cosine = Math.Cos(omega);
        var sine = Math.Sin(omega);
        var alpha = sine / (2 * filter.Q);
        var amplitude = Math.Pow(10, filter.GainDb / 40);
        double b0;
        double b1;
        double b2;
        double a0;
        double a1;
        double a2;
        switch (filter.Shape)
        {
            case FilterShapeV1.Notch:
                b0 = 1; b1 = -2 * cosine; b2 = 1; a0 = 1 + alpha; a1 = -2 * cosine; a2 = 1 - alpha;
                break;
            case FilterShapeV1.LowShelf:
            case FilterShapeV1.HighShelf:
                return Shelf(filter, frequency, sampleRate);
            default:
                b0 = 1 + alpha * amplitude; b1 = -2 * cosine; b2 = 1 - alpha * amplitude;
                a0 = 1 + alpha / amplitude; a1 = -2 * cosine; a2 = 1 - alpha / amplitude;
                break;
        }
        return Evaluate(b0, b1, b2, a0, a1, a2, frequency, sampleRate);
    }

    private static Complex Shelf(ParametricFilterV1 filter, double frequency, double sampleRate)
    {
        var amplitude = Math.Pow(10, filter.GainDb / 40);
        var omega = 2 * Math.PI * filter.FrequencyHz / sampleRate;
        var cosine = Math.Cos(omega);
        var alpha = Math.Sin(omega) * Math.Sqrt(2) / 2;
        var root = 2 * Math.Sqrt(amplitude) * alpha;
        double b0;
        double b1;
        double b2;
        double a0;
        double a1;
        double a2;
        if (filter.Shape == FilterShapeV1.HighShelf)
        {
            b0 = amplitude * ((amplitude + 1) + (amplitude - 1) * cosine + root);
            b1 = -2 * amplitude * ((amplitude - 1) + (amplitude + 1) * cosine);
            b2 = amplitude * ((amplitude + 1) + (amplitude - 1) * cosine - root);
            a0 = (amplitude + 1) - (amplitude - 1) * cosine + root;
            a1 = 2 * ((amplitude - 1) - (amplitude + 1) * cosine);
            a2 = (amplitude + 1) - (amplitude - 1) * cosine - root;
        }
        else
        {
            b0 = amplitude * ((amplitude + 1) - (amplitude - 1) * cosine + root);
            b1 = 2 * amplitude * ((amplitude - 1) - (amplitude + 1) * cosine);
            b2 = amplitude * ((amplitude + 1) - (amplitude - 1) * cosine - root);
            a0 = (amplitude + 1) + (amplitude - 1) * cosine + root;
            a1 = -2 * ((amplitude - 1) + (amplitude + 1) * cosine);
            a2 = (amplitude + 1) + (amplitude - 1) * cosine - root;
        }
        return Evaluate(b0, b1, b2, a0, a1, a2, frequency, sampleRate);
    }

    private static Complex Evaluate(double b0, double b1, double b2, double a0, double a1, double a2, double frequency, double sampleRate)
    {
        var z = Complex.FromPolarCoordinates(1, -2 * Math.PI * frequency / sampleRate);
        return (b0 + b1 * z + b2 * z * z) / (a0 + a1 * z + a2 * z * z);
    }
}
