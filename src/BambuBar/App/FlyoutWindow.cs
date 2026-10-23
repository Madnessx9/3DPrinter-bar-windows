using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using BambuBar.Platform;
using BambuBar.UI;

namespace BambuBar.App;

/// <summary>
/// The tray panel's window: a borderless flyout above the tray icon, frosted with acrylic on Windows 11 like the
/// system's own flyouts. It hides when you click elsewhere, the way a menu bar panel does.
/// </summary>
internal sealed class FlyoutWindow : Window
{
    private readonly MenuPanel _panel = new();
    private readonly System.Windows.Controls.Border _frame;
    private DateTime _hiddenAt;
    private bool _acrylic;
    private Native.RECT? _anchor;

    public FlyoutWindow()
    {
        Title = "Bambu Bar";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = true;
        // A DWM frame without a caption: keeps the system shadow, rounded corners and backdrop.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = Native.IsWindows11 ? new Thickness(-1) : new Thickness(0),
            UseAeroCaptionButtons = false,
        });
        _frame = new System.Windows.Controls.Border { Child = _panel, BorderThickness = new Thickness(Native.IsWindows11 ? 0 : 1) };
        Content = _frame;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // Out of Alt+Tab, like the system flyouts.
            var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr((style | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW));
            ApplyTheme();
        };
        Deactivated += (_, _) => HidePanel();
        SizeChanged += (_, _) => { if (IsVisible) Place(); };
        PreviewKeyDown += OnKey;
        Theme.Changed += ApplyTheme;
    }

    public void Toggle(Native.RECT? anchor)
    {
        if (IsVisible) { HidePanel(); return; }
        // The click on the tray icon is what took focus from the panel; don't reopen it straight away.
        if (DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(300)) return;
        ShowPanel(anchor);
    }

    public void ShowPanel(Native.RECT? anchor)
    {
        _anchor = anchor;
        _panel.Opened();
        Opacity = 0;
        Show();
        // Move to the right monitor first so WPF lays out at its DPI, then place by the final size.
        if (anchor is { } a) Native.SetWindowPos(Handle, IntPtr.Zero, a.Left, a.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        UpdateLayout();
        Place();
        Activate();
        Native.SetForegroundWindow(Handle);
        Live.Invalidate();
        Appear();
    }

    public void HidePanel()
    {
        if (!IsVisible) return;
        _hiddenAt = DateTime.UtcNow;
        Hide();
    }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>Above the tray icon, inside the work area, the way Windows places its own flyouts; beside or below
    /// it when the taskbar is on another edge.</summary>
    private void Place()
    {
        if (!Native.GetWindowRect(Handle, out var window)) return;
        int ax, ay;
        if (_anchor is { } a) { ax = (a.Left + a.Right) / 2; ay = (a.Top + a.Bottom) / 2; }
        else { Native.GetCursorPos(out var p); ax = p.X; ay = p.Y; }
        var work = Native.WorkAreaAt(ax, ay);
        var scale = Native.DpiAt(ax, ay) / 96.0;
        var margin = (int)Math.Round(12 * scale);
        int w = window.Width, h = window.Height;
        int x, y;
        switch (Native.TaskbarEdge())
        {
            case Native.ABE_TOP:
                x = ax - w / 2; y = work.Top + margin; break;
            case Native.ABE_LEFT:
                x = work.Left + margin; y = ay - h / 2; break;
            case Native.ABE_RIGHT:
                x = work.Right - w - margin; y = ay - h / 2; break;
            default:
                x = ax - w / 2; y = work.Bottom - h - margin; break;
        }
        x = Math.Clamp(x, work.Left + margin, Math.Max(work.Left + margin, work.Right - w - margin));
        y = Math.Clamp(y, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - h - margin));
        if (x != window.Left || y != window.Top)
            Native.SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>Slides up and fades in, like the system flyouts.</summary>
    private void Appear()
    {
        var duration = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 170 : 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var shift = new TranslateTransform(0, Native.TaskbarEdge() == Native.ABE_TOP ? -10 : 10);
        _frame.RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
    }

    private void ApplyTheme()
    {
        if (Handle == IntPtr.Zero) return;
        _acrylic = Backdrop.Apply(this, Backdrop.Kind.Acrylic, Theme.Dark);
        // Without acrylic the panel gets the flyout colour and a hairline, as on Windows 10.
        _frame.Background = _acrylic ? Brushes.Transparent : Theme.Brush(Theme.FlyoutColor);
        _frame.BorderBrush = Theme.Fill(0.12);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Escape:
                HidePanel(); e.Handled = true; break;
            case Key.O when ctrl:
                Shell.ShowMainWindow(AppSection.Overview); e.Handled = true; break;
            case Key.Q when ctrl:
                Shell.Quit(); e.Handled = true; break;
            case Key.R when ctrl:
                Services.PrinterStore.Shared.Refresh(); e.Handled = true; break;
            case Key.OemComma when ctrl:
                Shell.ShowMainWindow(AppSection.Settings); e.Handled = true; break;
        }
    }
}
