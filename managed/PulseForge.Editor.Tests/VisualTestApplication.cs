using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PulseForge.Editor;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(PulseForge.Editor.Tests.VisualTestApplication))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PulseForge.Editor.Tests;

public static class VisualTestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = false,
        });
}
