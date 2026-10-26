using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PulseForge.Contracts;

namespace PulseForge.Editor;

public sealed class StereoMeter : Control
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
        var bounds = Bounds.Deflate(new Thickness(4));
        DrawChannel(context, new Rect(bounds.X, bounds.Y, bounds.Width * 0.42, bounds.Height), telemetry?.PeakLeftDbFs ?? -120, telemetry?.RmsLeftDbFs ?? -120, Color.Parse("#65D9CC"));
        DrawChannel(context, new Rect(bounds.X + bounds.Width * 0.56, bounds.Y, bounds.Width * 0.42, bounds.Height), telemetry?.PeakRightDbFs ?? -120, telemetry?.RmsRightDbFs ?? -120, Color.Parse("#E8A95B"));
    }

    private static void DrawChannel(DrawingContext context, Rect bounds, double peakDb, double rmsDb, Color color)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0A1017")), bounds, 4);
        var peak = Math.Clamp((peakDb + 60) / 60, 0, 1);
        var rms = Math.Clamp((rmsDb + 60) / 60, 0, 1);
        var rmsRect = new Rect(bounds.X + 2, bounds.Bottom - bounds.Height * rms, bounds.Width - 4, bounds.Height * rms);
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(105, color.R, color.G, color.B)), rmsRect, 2);
        var peakY = bounds.Bottom - bounds.Height * peak;
        context.DrawLine(new Pen(new SolidColorBrush(peakDb > -1 ? Color.Parse("#F06B74") : color), 2), new Point(bounds.Left + 2, peakY), new Point(bounds.Right - 2, peakY));
        for (var db = -48; db <= 0; db += 12)
        {
            var y = bounds.Bottom - bounds.Height * (db + 60) / 60;
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#223043")), 1), new Point(bounds.Left, y), new Point(bounds.Right, y));
        }
    }
}
