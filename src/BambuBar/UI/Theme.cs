using System.Windows;
using System.Windows.Media;
using BambuBar.Models;
using Microsoft.Win32;

namespace BambuBar.UI;

/// <summary>
/// Colours and fonts. The app follows Windows' light or dark app mode; the tray icon follows the taskbar's own mode.
/// Views read these while they're built, so a theme change rebuilds them (<see cref="Live"/>). Widgets draw dark
/// whatever the mode, like the macOS app's widget previews, through <see cref="Override"/>.
/// </summary>
public static class Theme
{
    private static bool _appDark;
    [ThreadStatic] private static bool? _override;

    /// <summary>Bumped on every change, so views keyed on it rebuild.</summary>
    public static int Version { get; private set; }

    public static event Action? Changed;

    /// <summary>The taskbar (and tray) is dark: Windows' "default Windows mode".</summary>
    public static bool TaskbarDark { get; private set; } = true;

    public static bool Dark => _override ?? _appDark;

    /// <summary>The value an enclosing <see cref="Override"/> set, for views that rebuild later.</summary>
    public static bool? CurrentOverride => _override;

    static Theme() => Read();

    public static void Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var appDark = key?.GetValue("AppsUseLightTheme") is int apps ? apps == 0 : false;
        var taskbarDark = key?.GetValue("SystemUsesLightTheme") is int system ? system == 0 : true;
        if (Services.AppArguments.Current.Has("--dark")) appDark = true;
        if (Services.AppArguments.Current.Has("--light")) appDark = false;
        if (appDark == _appDark && taskbarDark == TaskbarDark && Version > 0) return;
        _appDark = appDark;
        TaskbarDark = taskbarDark;
        Version++;
        Changed?.Invoke();
    }

    /// <summary>Builds what's inside the scope in the given mode.</summary>
    public static IDisposable Override(bool? dark)
    {
        var previous = _override;
        _override = dark;
        return new Restore(() => _override = previous);
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    // MARK: - Text and fills

    public static Color PrimaryColor => Dark ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x1D, 0x1D, 0x1F);
    public static Brush Primary => Brush(PrimaryColor);
    public static Brush Secondary => Brush(PrimaryColor, Dark ? 0.62 : 0.6);
    public static Brush Tertiary => Brush(PrimaryColor, Dark ? 0.38 : 0.4);

    /// <summary>The primary colour at an opacity: the faint fills and hairlines of panels and tracks.</summary>
    public static Brush Fill(double opacity) => Brush(PrimaryColor, opacity);

    public static Color WindowColor => Dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
    /// <summary>Solid stand-in for the acrylic flyout on Windows 10.</summary>
    public static Color FlyoutColor => Dark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Color.FromRgb(0xF7, 0xF7, 0xF7);

    // MARK: - Fonts

    public static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");
    /// <summary>Large numbers (the macOS app uses SF Rounded).</summary>
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // MARK: - Brushes

    private static readonly Dictionary<(Color, double), SolidColorBrush> Brushes = [];

    public static SolidColorBrush Brush(Color color, double opacity = 1)
    {
        if (Brushes.TryGetValue((color, opacity), out var hit)) return hit;
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(color.A * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B));
        brush.Freeze();
        if (Brushes.Count < 4096) Brushes[(color, opacity)] = brush;
        return brush;
    }

    public static Color WithOpacity(this Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(color.A * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B);
}

/// <summary>The macOS app's colours.</summary>
public static class Palette
{
    private static Color Rgb(double r, double g, double b) => Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));

    /// <summary>Bambu-ish green, a touch brighter so it reads on dark glass.</summary>
    public static readonly Color Accent = Rgb(0.13, 0.80, 0.42);
    public static readonly Color AccentDeep = Rgb(0.00, 0.60, 0.29);
    public static readonly Color Printing = Accent;
    public static readonly Color Preparing = Rgb(0.36, 0.66, 1.00);
    public static readonly Color Paused = Rgb(1.00, 0.72, 0.24);
    public static readonly Color Failed = Rgb(1.00, 0.36, 0.36);
    public static readonly Color Finished = Rgb(0.42, 0.86, 0.62);
    public static readonly Color IdleGray = Rgb(0.62, 0.66, 0.70);
    public static readonly Color Nozzle = Rgb(1.00, 0.48, 0.30);
    public static readonly Color Bed = Rgb(1.00, 0.72, 0.30);
    public static readonly Color Chamber = Rgb(0.55, 0.70, 1.00);
    public static readonly Color Yellow = Rgb(1.00, 0.80, 0.00);

    public static Color ColorFor(JobState state) => state switch
    {
        JobState.Idle => IdleGray,
        JobState.Preparing => Preparing,
        JobState.Printing => Printing,
        JobState.Paused => Paused,
        JobState.Finished => Finished,
        _ => Failed,
    };

    public static Color ColorFor(PrinterStatus status) =>
        status.State == JobState.Failed && status.Canceled ? ColorFor(JobState.Idle) : ColorFor(status.State);

    /// <summary>A Bambu "RRGGBBAA" filament colour, or null for an empty or unreadable one.</summary>
    public static Color? Filament(string? hex) =>
        BambuColor.Parse(hex) is { } c ? Color.FromArgb(c.A, c.R, c.G, c.B) : null;
}
