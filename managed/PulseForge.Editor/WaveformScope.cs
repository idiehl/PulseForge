using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PulseForge.Contracts;

namespace PulseForge.Editor;

public sealed class WaveformScope : Control
{
    private static readonly IBrush Grid = new SolidColorBrush(Color.Parse("#1C2939"));
    private static readonly IBrush Left = new SolidColorBrush(Color.Parse("#65D9CC"));
    private static readonly IBrush Right = new SolidColorBrush(Color.Parse("#E8A95B"));
    private static readonly IBrush Pre = new SolidColorBrush(Color.Parse("#52657C"));
    private TelemetryFrameV1? telemetry;
    private bool frozen;
    private double horizontalZoom = 1;
    private double verticalGain = 1;

    public bool Frozen
    {
        get => frozen;
        set => frozen = value;
    }

    public double HorizontalZoom
    {
        get => horizontalZoom;
        set
        {
            horizontalZoom = Math.Clamp(value, 1, 8);
            InvalidateVisual();
        }
    }

    public double VerticalGain
    {
        get => verticalGain;
        set
        {
            verticalGain = Math.Clamp(value, 0.5, 4);
            InvalidateVisual();
        }
    }

    public void SetTelemetry(TelemetryFrameV1? frame)
    {
        if (frozen) return;
        telemetry = frame;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds.Deflate(new Thickness(1));
        context.FillRectangle(new SolidColorBrush(Color.Parse("#090F16")), bounds, 5);
        for (var index = 1; index < 8; index++)
        {
            var x = bounds.Left + bounds.Width * index / 8;
            context.DrawLine(new Pen(Grid, 1), new Point(x, bounds.Top), new Point(x, bounds.Bottom));
        }
        context.DrawLine(new Pen(Grid, 1), new Point(bounds.Left, bounds.Center.Y), new Point(bounds.Right, bounds.Center.Y));
        context.DrawLine(new Pen(Grid, 1), new Point(bounds.Left, bounds.Top + bounds.Height * 0.25), new Point(bounds.Right, bounds.Top + bounds.Height * 0.25));
        context.DrawLine(new Pen(Grid, 1), new Point(bounds.Left, bounds.Top + bounds.Height * 0.75), new Point(bounds.Right, bounds.Top + bounds.Height * 0.75));
        if (telemetry is null) return;
        DrawWave(context, bounds, telemetry.PreLeft, Pre, 0.55, 0.28);
        DrawWave(context, bounds, telemetry.PreRight, Pre, 0.55, 0.72);
        DrawWave(context, bounds, telemetry.PostLeft, Left, 1.8, 0.28);
        DrawWave(context, bounds, telemetry.PostRight, Right, 1.8, 0.72);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        HorizontalZoom *= e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
    }

    private void DrawWave(DrawingContext context, Rect bounds, float[] samples, IBrush brush, double thickness, double centerFraction)
    {
        if (samples.Length < 2) return;
        var visible = Math.Max(32, (int)(samples.Length / horizontalZoom));
        var trigger = FindTrigger(samples, visible);
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            for (var index = 0; index < visible; index++)
            {
                var source = Math.Min(samples.Length - 1, trigger + index);
                var x = bounds.Left + index * bounds.Width / (visible - 1);
                var y = bounds.Top + bounds.Height * centerFraction - Math.Clamp(samples[source] * verticalGain, -1, 1) * bounds.Height * 0.2;
                if (index == 0) stream.BeginFigure(new Point(x, y), false);
                else stream.LineTo(new Point(x, y));
            }
        }
        context.DrawGeometry(null, new Pen(brush, thickness), geometry);
    }

    private static int FindTrigger(float[] samples, int visible)
    {
        var limit = Math.Max(1, samples.Length - visible);
        for (var index = 1; index < limit; index++)
        {
            if (samples[index - 1] <= 0 && samples[index] > 0) return index;
        }
        return 0;
    }
}
