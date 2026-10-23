using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BambuBar.Platform;

/// <summary>Windows 11's system backdrops: Mica behind the main window, acrylic behind the tray panel, with the title
/// bar following the app's light or dark look. On Windows 10 windows keep their solid background.</summary>
public static class Backdrop
{
    public enum Kind { Mica, Acrylic }

    /// <summary>Applies the backdrop once the window has a handle. Returns false where it isn't supported.</summary>
    public static bool Apply(Window window, Kind kind, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        SetDark(hwnd, dark);
        if (!Native.IsWindows11 || SystemParameters.HighContrast) return false;
        var corner = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        var type = kind == Kind.Mica ? Native.DWMSBT_MAINWINDOW : Native.DWMSBT_TRANSIENTWINDOW;
        if (Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) != 0) return false;
        // The backdrop shows through wherever WPF draws nothing.
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target }) target.BackgroundColor = Colors.Transparent;
        var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        window.Background = Brushes.Transparent;
        return true;
    }

    public static void SetDark(IntPtr hwnd, bool dark)
    {
        var value = dark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }
}
