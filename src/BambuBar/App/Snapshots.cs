using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BambuBar.Models;
using BambuBar.Services;
using BambuBar.UI;
using BambuBar.UI.Widgets;

namespace BambuBar.App;

/// <summary>
/// <c>--snapshot &lt;dir&gt;</c> (with <c>--demo</c>): renders the tray panel, the widgets, every page of the window and
/// the tray icon's states to PNG files, then quits. Used for README images and for checking the UI on machines nobody
/// is looking at (CI). Add <c>--dark</c> or <c>--light</c> for either look.
/// </summary>
internal static class Snapshots
{
    private const double Scale = 2;

    public static void Run(string dir, IShell shell) => _ = RunAsync(dir, shell);

    private static async Task RunAsync(string dir, IShell shell)
    {
        Directory.CreateDirectory(dir);
        var suffix = Theme.Dark ? "dark" : "light";
        var written = new List<string>();
        try
        {
            // Let the demo emit its first status and the printer model load.
            await Task.Delay(1500);
            var store = PrinterStore.Shared;

            // The tray panel, on the flyout's colour (acrylic is the desktop's doing, not the app's).
            var panel = new MenuPanel(showsAllOverride: false);
            panel.Opened();
            written.Add(await Render(Card(panel, Theme.FlyoutColor), Path.Combine(dir, $"panel-{suffix}.png")));
            if (store.HasMultiplePrinters)
                written.Add(await Render(Card(new MenuPanel(showsAllOverride: true), Theme.FlyoutColor), Path.Combine(dir, $"panel-fleet-{suffix}.png")));

            // The three widgets on a wallpaper.
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24) };
            foreach (var size in Enum.GetValues<WidgetSize>())
            {
                var widget = WidgetViews.Framed(size, store.Snapshot, DateTime.UtcNow);
                widget.Margin = new Thickness(0, 0, 18, 0);
                widget.VerticalAlignment = VerticalAlignment.Top;
                row.Children.Add(widget);
            }
            var wallpaper = new Border
            {
                Child = row,
                Background = new LinearGradientBrush(Color.FromRgb(140, 107, 84), Color.FromRgb(56, 43, 41), 45),
            };
            written.Add(await Render(wallpaper, Path.Combine(dir, $"widgets-{suffix}.png")));

            // Every page of the window.
            var window = new MainWindow { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            var pages = new List<(string Name, AppSection Section, bool Adding)>();
            if (store.HasMultiplePrinters) pages.Add(("fleet", AppSection.Fleet, false));
            pages.AddRange([("printer", AppSection.Overview, false), ("filament", AppSection.Filament, false), ("widgets", AppSection.Widgets, false),
                ("settings", AppSection.Settings, false), ("about", AppSection.About, false), ("add-printer", AppSection.Overview, true)]);
            foreach (var (name, section, adding) in pages)
            {
                Navigation.Shared.AddingPrinter = adding;
                Navigation.Shared.Section = section;
                await Settle();
                written.Add(await RenderWindow(window, Path.Combine(dir, $"window-{name}-{suffix}.png")));
            }
            Navigation.Shared.AddingPrinter = false;
            window.Quitting = true;
            window.Close();

            written.Add(TrayIcons(Path.Combine(dir, "tray-icons.png")));
            File.WriteAllLines(Path.Combine(dir, $"snapshots-{suffix}.txt"), written.Select(Path.GetFileName)!);
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(dir, $"snapshot-error-{suffix}.txt"), e.ToString());
            Environment.ExitCode = 1;
        }
        shell.Quit();
    }

    private static Border Card(UIElement content, Color background) => new()
    {
        Child = content, Background = new SolidColorBrush(background), CornerRadius = new CornerRadius(8),
        BorderBrush = Theme.Fill(0.12), BorderThickness = new Thickness(1),
    };

    /// <summary>Waits until rebuilds, layout and rendering are done.</summary>
    private static async Task Settle()
    {
        Live.Invalidate();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(300);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Renders an element at its natural size, hosted in an off-screen window so it lays out and updates like
    /// it does on screen.</summary>
    private static async Task<string> Render(FrameworkElement element, string path)
    {
        var host = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, SizeToContent = SizeToContent.WidthAndHeight,
            Left = -20000, Top = -20000, Content = element, AllowsTransparency = true, Background = Brushes.Transparent,
        };
        host.Show();
        await Settle();
        Save(element, element.ActualWidth, element.ActualHeight, null, path);
        host.Content = null;
        host.Close();
        return path;
    }

    /// <summary>The window's content on its plain background (Mica comes from the desktop compositor, which a render
    /// can't capture).</summary>
    private static Task<string> RenderWindow(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        Save(content, content.ActualWidth, content.ActualHeight, Theme.WindowColor, path);
        return Task.FromResult(path);
    }

    private static void Save(Visual visual, double width, double height, Color? background, string path)
    {
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, width, height);
            if (background is { } b) dc.DrawRectangle(new SolidColorBrush(b), null, bounds);
            dc.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, bounds);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * Scale), (int)Math.Ceiling(height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>Every tray icon state at 16, 24 and 32 pixels, on a dark and a light taskbar.</summary>
    private static string TrayIcons(string path)
    {
        var states = new List<TrayIconState>
        {
            new(null, 0, false, false, null, "", true),
            new(null, 0, false, true, null, "", true),
            new(JobState.Idle, 0, false, false, null, "", true),
            new(JobState.Preparing, 0, false, false, null, "", true),
            new(JobState.Printing, 6, false, false, null, "", true),
            new(JobState.Printing, 14, false, false, null, "", true),
            new(JobState.Paused, 13, false, false, null, "", true),
            new(JobState.Paused, 13, true, false, null, "", true),
            new(JobState.Finished, 20, false, false, null, "", true),
            new(JobState.Failed, 9, false, false, null, "", true),
            new(JobState.Printing, 8, false, false, "42", "", true),
            new(JobState.Printing, 19, false, false, "100", "", true),
            new(JobState.Printing, 12, false, false, "2h", "", true),
            new(JobState.Printing, 17, true, false, "45m", "", true),
        };
        int[] sizes = [16, 24, 32];
        const int pad = 8;
        var cell = 32 + pad * 2;
        var width = states.Count * cell;
        var height = sizes.Length * 2 * cell;
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            for (var band = 0; band < 2; band++)
            {
                var dark = band == 0;
                dc.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(0x1C, 0x1C, 0x1C) : Color.FromRgb(0xEE, 0xEE, 0xEE)), null,
                    new Rect(0, band * sizes.Length * cell, width, sizes.Length * cell));
                for (var r = 0; r < sizes.Length; r++)
                {
                    for (var c = 0; c < states.Count; c++)
                    {
                        var image = TrayIconArt.Render(states[c] with { Dark = dark }, sizes[r]);
                        var x = c * cell + (cell - sizes[r]) / 2.0;
                        var y = (band * sizes.Length + r) * cell + (cell - sizes[r]) / 2.0;
                        dc.DrawImage(image, new Rect(x, y, sizes[r], sizes[r]));
                    }
                }
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }
}
