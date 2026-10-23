using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace BambuBar.Platform;

/// <summary>
/// The notification-area icon, through Shell_NotifyIcon directly (no WinForms). A hidden top-level window receives
/// its clicks, and the broadcasts that matter to it: Explorer restarting (the icon has to be added again), DPI and
/// display changes (the icon is redrawn at the new size) and the light/dark switch.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = Native.WM_APP + 1;
    private const int IconId = 1;

    private readonly HwndSource _window;
    private readonly int _taskbarCreated;
    private IntPtr _icon;
    private string _tip = "Bambu Bar";
    private bool _added;

    /// <summary>Left click or Enter/Space on the icon.</summary>
    public event Action? Activated;
    /// <summary>Right click or the menu key, at a point in physical pixels.</summary>
    public event Action<int, int>? ContextMenuRequested;
    /// <summary>The icon has to be drawn again: DPI, display or theme changed, or Explorer restarted.</summary>
    public event Action? RedrawNeeded;
    /// <summary>Windows switched between light and dark.</summary>
    public event Action? ThemeChanged;

    public TrayIcon()
    {
        // A real (never shown) top-level window: message-only windows don't receive broadcasts like TaskbarCreated.
        _window = new HwndSource(new HwndSourceParameters("Bambu Bar tray") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WndProc);
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    }

    public IntPtr Handle => _window.Handle;

    /// <summary>Shows <paramref name="icon"/> (an HICON this object now owns) with a tooltip of at most 127 characters.</summary>
    public void Update(IntPtr icon, string tip)
    {
        var old = _icon;
        _icon = icon;
        _tip = tip.Length > 127 ? tip[..126] + "…" : tip;
        var data = Data(Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP | Native.NIF_MESSAGE);
        if (!_added)
        {
            _added = Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
            if (_added)
            {
                data.uVersion = Native.NOTIFYICON_VERSION_4;
                Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref data);
            }
        }
        else if (!Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data))
        {
            // Explorer may have dropped the icon without announcing it (it was busy restarting); add it again.
            _added = false;
            Update(icon, tip);
            return;
        }
        if (old != IntPtr.Zero && old != icon) Native.DestroyIcon(old);
    }

    /// <summary>Where the icon is on screen in physical pixels, or null if Windows won't say (it's in the overflow
    /// area, or the taskbar is hidden).</summary>
    public Native.RECT? Bounds()
    {
        var id = new Native.NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<Native.NOTIFYICONIDENTIFIER>(), hWnd = Handle, uID = IconId };
        return Native.Shell_NotifyIconGetRect(ref id, out var rect) == 0 && rect.Width > 0 ? rect : null;
    }

    private Native.NOTIFYICONDATA Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == CallbackMessage)
        {
            // Version 4: the event is in the low word of lParam, the anchor point in wParam.
            var events = (int)(lParam.ToInt64() & 0xFFFF);
            var x = (short)(wParam.ToInt64() & 0xFFFF);
            var y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            switch (events)
            {
                case Native.NIN_SELECT:
                case Native.NIN_KEYSELECT:
                    Activated?.Invoke();
                    break;
                case Native.WM_CONTEXTMENU:
                    ContextMenuRequested?.Invoke(x, y);
                    break;
            }
            handled = true;
        }
        else if (message == _taskbarCreated)
        {
            _added = false;
            RedrawNeeded?.Invoke();
        }
        else if (message is Native.WM_DPICHANGED or Native.WM_DISPLAYCHANGE)
        {
            RedrawNeeded?.Invoke();
        }
        else if (message == Native.WM_SETTINGCHANGE && lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            ThemeChanged?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data(0);
            Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
            _added = false;
        }
        if (_icon != IntPtr.Zero) Native.DestroyIcon(_icon);
        _icon = IntPtr.Zero;
        _window.Dispose();
    }
}
