using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PulseForge.Editor;

internal sealed class SafetyConfirmationDialog : Window
{
    internal SafetyConfirmationDialog()
    {
        Title = "Confirm unguarded boost";
        Width = 470;
        Height = 255;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#101720"));
        var cancel = new Button { Content = "Keep limiter armed", MinWidth = 140 };
        var confirm = new Button
        {
            Content = "Bypass limiter anyway",
            MinWidth = 150,
            Background = new SolidColorBrush(Color.Parse("#5A2930")),
            Foreground = new SolidColorBrush(Color.Parse("#FFD6D9")),
        };
        cancel.Click += (_, _) => Close(false);
        confirm.Click += (_, _) => Close(true);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, confirm },
        };
        Grid.SetRow(actions, 1);

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(20),
            Children =
            {
                new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = "MASTER BOOST IS ABOVE 0 dB", Foreground = new SolidColorBrush(Color.Parse("#F2C76D")), FontWeight = FontWeight.Bold, FontSize = 13 },
                        new TextBlock
                        {
                            Text = "Disabling the true-peak limiter can produce clipping and unexpectedly dangerous listening levels. PulseForge will keep the overload warning visible, but it cannot protect the output.",
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = new SolidColorBrush(Color.Parse("#C1CDDA")),
                        },
                        new TextBlock { Text = "This is an expert override, not a calibration or hearing-safety guarantee.", Foreground = new SolidColorBrush(Color.Parse("#7F91A7")), FontSize = 10 },
                    },
                },
                actions,
            },
        };
    }
}
