using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace PulseForge.Editor.Tests;

public sealed class WorkstationRenderTests
{
    [AvaloniaTheory]
    [InlineData(1280, 720, 1.0)]
    [InlineData(1280, 720, 1.5)]
    [InlineData(1280, 720, 2.0)]
    [InlineData(2560, 1440, 1.0)]
    [InlineData(2560, 1440, 1.5)]
    [InlineData(2560, 1440, 2.0)]
    public void WorkstationRendersAtRequiredWindowsScaling(int physicalWidth, int physicalHeight, double windowsScale)
    {
        var logicalWidth = physicalWidth / windowsScale;
        var logicalHeight = physicalHeight / windowsScale;
        var window = new MainWindow(connectRuntime: false)
        {
            Width = logicalWidth,
            Height = logicalHeight,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.InRange(frame.PixelSize.Width, (int)Math.Floor(logicalWidth) - 2, (int)Math.Ceiling(logicalWidth) + 2);
        Assert.InRange(frame.PixelSize.Height, (int)Math.Floor(logicalHeight) - 2, (int)Math.Ceiling(logicalHeight) + 2);

        foreach (var controlName in new[]
        {
            "RouteButton",
            "ModeList",
            "EqGraph",
            "WaveformView",
            "SelectedBandTitle",
            "MasterGainSlider",
            "OutputMeter",
            "StatusText",
        })
        {
            var control = window.FindControl<Control>(controlName);
            Assert.NotNull(control);
            Assert.True(control.IsEffectivelyVisible, $"{controlName} is not visible at {physicalWidth}x{physicalHeight} / {windowsScale:P0}.");
            Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0, $"{controlName} has no layout bounds.");
        }

        var output = GetOutputDirectory();
        Directory.CreateDirectory(output);
        var scalePercent = (int)Math.Round(windowsScale * 100);
        frame.Save(Path.Combine(output, $"pulseforge-{physicalWidth}x{physicalHeight}-{scalePercent}.png"));
        window.Close();
    }

    private static string GetOutputDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PulseForge.slnx")))
        {
            directory = directory.Parent;
        }
        if (directory is null) throw new InvalidOperationException("PulseForge repository root was not found.");
        return Path.Combine(directory.FullName, "out", "visual-tests");
    }
}
