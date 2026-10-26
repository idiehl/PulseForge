using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PulseForge.Contracts;

namespace PulseForge.Editor;

public sealed class PhaseScope : Control
{
    private TelemetryFrameV1? telemetry;

    public void SetTelemetry(TelemetryFrameV1? frame)
    {
        telemetry = frame;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds.Deflate(new Thickness(8));
        var grid = new Pen(new SolidColorBrush(Color.Parse("#223043")), 1);
        var center = bounds.Center;
        var radius = Math.Min(bounds.Width, bounds.Height) * 0.43;
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#090F16")), grid, center, radius, radius);
        context.DrawLine(grid, new Point(center.X - radius, center.Y), new Point(center.X + radius, center.Y));
        context.DrawLine(grid, new Point(center.X, center.Y - radius), new Point(center.X, center.Y + radius));
        context.DrawLine(grid, new Point(center.X - radius * 0.7, center.Y + radius * 0.7), new Point(center.X + radius * 0.7, center.Y - radius * 0.7));
        context.DrawLine(grid, new Point(center.X - radius * 0.7, center.Y - radius * 0.7), new Point(center.X + radius * 0.7, center.Y + radius * 0.7));
        if (telemetry is null) return;
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            for (var index = 0; index < Math.Min(telemetry.PostLeft.Length, telemetry.PostRight.Length); index += 2)
            {
                var left = telemetry.PostLeft[index];
                var right = telemetry.PostRight[index];
                var side = Math.Clamp((left - right) * 0.7, -1, 1);
                var mid = Math.Clamp((left + right) * 0.7, -1, 1);
                var point = new Point(center.X + side * radius, center.Y - mid * radius);
                if (index == 0) stream.BeginFigure(point, false);
                else stream.LineTo(point);
            }
        }
        var color = telemetry.Correlation < 0 ? Color.Parse("#E76F78") : Color.Parse("#65D9CC");
        context.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.25), geometry);
        var bar = new Rect(bounds.Right - 12, bounds.Top, 7, bounds.Height);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#172231")), bar, 3);
        var normalized = (telemetry.Correlation + 1) * 0.5;
        context.FillRectangle(new SolidColorBrush(color), new Rect(bar.X, bar.Bottom - bar.Height * normalized, bar.Width, bar.Height * normalized), 3);
    }
}
