using System.Numerics;

namespace PulseForge.Engine.Client;

internal static class SpectrumAnalyzer
{
    internal static (float[] Left, float[] Right) Calculate(float[] left, float[] right)
    {
        var leftBuffer = Prepare(left);
        var rightBuffer = Prepare(right);
        Transform(leftBuffer);
        Transform(rightBuffer);
        return (ToDecibels(leftBuffer), ToDecibels(rightBuffer));
    }

    private static Complex[] Prepare(float[] source)
    {
        var buffer = new Complex[512];
        var count = Math.Min(source.Length, buffer.Length);
        for (var index = 0; index < count; index++)
        {
            var window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (buffer.Length - 1));
            buffer[index] = new Complex(source[index] * window, 0);
        }
        return buffer;
    }

    private static void Transform(Complex[] buffer)
    {
        var length = buffer.Length;
        for (var index = 1; index < length; index++)
        {
            var bit = length >> 1;
            var swapped = index;
            while ((swapped & bit) != 0)
            {
                swapped ^= bit;
                bit >>= 1;
            }
            swapped ^= bit;
            if (index < swapped) (buffer[index], buffer[swapped]) = (buffer[swapped], buffer[index]);
        }
        for (var size = 2; size <= length; size <<= 1)
        {
            var angle = -2 * Math.PI / size;
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < length; start += size)
            {
                var rotation = Complex.One;
                for (var offset = 0; offset < size / 2; offset++)
                {
                    var even = buffer[start + offset];
                    var odd = buffer[start + offset + size / 2] * rotation;
                    buffer[start + offset] = even + odd;
                    buffer[start + offset + size / 2] = even - odd;
                    rotation *= step;
                }
            }
        }
    }

    private static float[] ToDecibels(Complex[] buffer)
    {
        var result = new float[256];
        for (var index = 0; index < result.Length; index++)
        {
            var amplitude = buffer[index].Magnitude * 2 / buffer.Length;
            result[index] = (float)Math.Max(-120, 20 * Math.Log10(Math.Max(amplitude, 1e-6)));
        }
        return result;
    }
}
