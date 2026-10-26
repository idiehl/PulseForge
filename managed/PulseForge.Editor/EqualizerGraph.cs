using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PulseForge.Contracts;

namespace PulseForge.Editor;

public sealed class EqualizerGraph : Control
{
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#243143"));
    private static readonly IBrush MinorGridBrush = new SolidColorBrush(Color.Parse("#172231"));
    private static readonly IBrush LeftBrush = new SolidColorBrush(Color.Parse("#65D9CC"));
    private static readonly IBrush RightBrush = new SolidColorBrush(Color.Parse("#E8A95B"));
    private static readonly IBrush PreSpectrumBrush = new SolidColorBrush(Color.Parse("#45576A7E"));
    private static readonly IBrush PostSpectrumBrush = new SolidColorBrush(Color.Parse("#66728EA8"));
    private static readonly IBrush PhaseBrush = new SolidColorBrush(Color.Parse("#668E9DB0"));
    private static readonly IBrush DisabledBrush = new SolidColorBrush(Color.Parse("#566578"));
    private OutputModeV1? mode;
    private TelemetryFrameV1? telemetry;
    private bool showingLeft = true;
    private int selectedBand;
    private bool dragging;

    public event EventHandler<FilterGraphEditEventArgs>? FilterEdited;
    public event EventHandler<int>? SelectedBandChanged;

    public int SelectedBand
    {
        get => selectedBand;
        set
        {
            selectedBand = Math.Clamp(value, 0, 11);
            InvalidateVisual();
        }
    }

    public void SetData(OutputModeV1? outputMode, bool leftChannel, TelemetryFrameV1? frame)
    {
        mode = outputMode;
        showingLeft = leftChannel;
        telemetry = frame;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds.Deflate(new Thickness(1));
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0A1017")), bounds, 6);
        DrawGrid(context, bounds);
        if (mode is null) return;
        DrawSpectrum(context, bounds);
        DrawPhaseResponse(context, bounds);
        DrawResponse(context, bounds);
        DrawNodes(context, bounds);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (mode is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(this);
        var channel = showingLeft ? mode.Left : mode.Right;
        var nearest = -1;
        var nearestDistance = 18.0;
        for (var index = 0; index < channel.ParametricFilters.Count; index++)
        {
            var filter = channel.ParametricFilters[index];
            var node = new Point(FrequencyToX(filter.FrequencyHz, Bounds), GainToY(filter.GainDb, Bounds));
            var distance = Math.Sqrt(Math.Pow(node.X - point.X, 2) + Math.Pow(node.Y - point.Y, 2));
            if (distance < nearestDistance)
            {
                nearest = index;
                nearestDistance = distance;
            }
        }
        if (nearest >= 0)
        {
            selectedBand = nearest;
            SelectedBandChanged?.Invoke(this, selectedBand);
            dragging = true;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!dragging || mode is null) return;
        var point = e.GetPosition(this);
        var frequency = XToFrequency(point.X, Bounds);
        var gain = YToGain(point.Y, Bounds);
        FilterEdited?.Invoke(this, new FilterGraphEditEventArgs(selectedBand, frequency, gain));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragging)
        {
            dragging = false;
            e.Pointer.Capture(null);
        }
    }

    private static void DrawGrid(DrawingContext context, Rect bounds)
    {
        foreach (var gain in new[] { -18, -12, -6, 0, 6, 12, 18 })
        {
            var y = GainToY(gain, bounds);
            context.DrawLine(new Pen(gain == 0 ? GridBrush : MinorGridBrush, gain == 0 ? 1.2 : 1), new Point(bounds.Left, y), new Point(bounds.Right, y));
        }
        foreach (var frequency in new[] { 20, 30, 50, 100, 200, 500, 1_000, 2_000, 5_000, 10_000, 20_000 })
        {
            var x = FrequencyToX(frequency, bounds);
            context.DrawLine(new Pen(frequency is 100 or 1_000 or 10_000 ? GridBrush : MinorGridBrush, 1), new Point(x, bounds.Top), new Point(x, bounds.Bottom));
        }
    }

    private void DrawSpectrum(DrawingContext context, Rect bounds)
    {
        if (telemetry is null) return;
        var preSpectrum = showingLeft ? telemetry.PreSpectrumLeftDb : telemetry.PreSpectrumRightDb;
        var postSpectrum = showingLeft ? telemetry.SpectrumLeftDb : telemetry.SpectrumRightDb;
        DrawSpectrumCurve(context, bounds, preSpectrum, PreSpectrumBrush, 1);
        DrawSpectrumCurve(context, bounds, postSpectrum, PostSpectrumBrush, 1.4);
    }

    private void DrawSpectrumCurve(DrawingContext context, Rect bounds, float[] spectrum, IBrush brush, double thickness)
    {
        if (telemetry is null) return;
        if (spectrum.Length == 0) return;
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            var started = false;
            for (var index = 1; index < spectrum.Length; index++)
            {
                var frequency = index * telemetry.SampleRate / 512.0;
                if (frequency is < 20 or > 20_000) continue;
                var x = FrequencyToX(frequency, bounds);
                var normalized = Math.Clamp((spectrum[index] + 90) / 90.0, 0, 1);
                var y = bounds.Bottom - normalized * bounds.Height * 0.72;
                if (!started)
                {
                    stream.BeginFigure(new Point(x, y), false);
                    started = true;
                }
                else stream.LineTo(new Point(x, y));
            }
        }
        context.DrawGeometry(null, new Pen(brush, thickness), geometry);
    }

    private void DrawPhaseResponse(DrawingContext context, Rect bounds)
    {
        if (mode is null) return;
        var channel = showingLeft ? mode.Left : mode.Right;
        var sampleRate = telemetry?.SampleRate ?? 48_000;
        Point? previous = null;
        double? previousPhase = null;
        var pen = new Pen(PhaseBrush, 1);
        for (var pixel = 0; pixel <= Math.Max(1, (int)bounds.Width); pixel += 4)
        {
            var x = bounds.Left + pixel;
            var frequency = XToFrequency(x, bounds);
            var phase = FilterResponse.CalculatePhaseDegrees(channel, frequency, sampleRate);
            var y = bounds.Center.Y - phase / 180 * bounds.Height * 0.44;
            var point = new Point(x, y);
            if (previous is Point start && previousPhase is double prior && Math.Abs(phase - prior) < 120)
            {
                context.DrawLine(pen, start, point);
            }
            previous = point;
            previousPhase = phase;
        }
    }

    private void DrawResponse(DrawingContext context, Rect bounds)
    {
        if (mode is null) return;
        var channel = showingLeft ? mode.Left : mode.Right;
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            for (var pixel = 0; pixel <= Math.Max(1, (int)bounds.Width); pixel += 2)
            {
                var x = bounds.Left + pixel;
                var frequency = XToFrequency(x, bounds);
                var gain = FilterResponse.CalculateDb(channel, frequency, telemetry?.SampleRate ?? 48_000);
                var point = new Point(x, GainToY(Math.Clamp(gain, -24, 24), bounds));
                if (pixel == 0) stream.BeginFigure(point, false);
                else stream.LineTo(point);
            }
        }
        context.DrawGeometry(null, new Pen(showingLeft ? LeftBrush : RightBrush, 2.3), geometry);
    }

    private void DrawNodes(DrawingContext context, Rect bounds)
    {
        if (mode is null) return;
        var channel = showingLeft ? mode.Left : mode.Right;
        for (var index = 0; index < channel.ParametricFilters.Count; index++)
        {
            var filter = channel.ParametricFilters[index];
            var center = new Point(FrequencyToX(filter.FrequencyHz, bounds), GainToY(filter.GainDb, bounds));
            var brush = filter.Enabled ? showingLeft ? LeftBrush : RightBrush : DisabledBrush;
            var radius = index == selectedBand ? 7.5 : 5.5;
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#0A1017")), new Pen(brush, index == selectedBand ? 2.5 : 1.5), center, radius, radius);
            if (index == selectedBand) context.DrawEllipse(null, new Pen(Brushes.White, 1), center, radius + 3, radius + 3);
        }
    }

    private static double FrequencyToX(double frequency, Rect bounds)
    {
        var normalized = Math.Log10(Math.Clamp(frequency, 20, 20_000) / 20.0) / 3.0;
        return bounds.Left + normalized * bounds.Width;
    }

    private static double XToFrequency(double x, Rect bounds)
    {
        var normalized = Math.Clamp((x - bounds.Left) / Math.Max(1, bounds.Width), 0, 1);
        return 20 * Math.Pow(1_000, normalized);
    }

    private static double GainToY(double gain, Rect bounds) => bounds.Top + (24 - Math.Clamp(gain, -24, 24)) / 48 * bounds.Height;
    private static double YToGain(double y, Rect bounds) => Math.Clamp(24 - (y - bounds.Top) / Math.Max(1, bounds.Height) * 48, -24, 18);
}

public sealed class FilterGraphEditEventArgs(int index, double frequencyHz, double gainDb) : EventArgs
{
    public int Index { get; } = index;
    public double FrequencyHz { get; } = frequencyHz;
    public double GainDb { get; } = gainDb;
}
