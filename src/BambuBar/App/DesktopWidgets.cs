using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BambuBar.Models;
using BambuBar.Platform;
using BambuBar.Services;
using BambuBar.UI;
using BambuBar.UI.Widgets;

namespace BambuBar.App;

/// <summary>
/// Desktop widgets. macOS has WidgetKit; Windows has no widget API an app like this can put a live view in, so each
/// widget is a small borderless window of its own that draws the same layouts. They sit on the desktop under other
/// windows (or above them, if you'd rather), stay where you drag them, and each can follow a different printer.
/// </summary>
public sealed class DesktopWidgets
{
    public static DesktopWidgets Shared { get; } = new();

    private const string SettingsKey = "desktopWidgets";

    public sealed record Placement
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public WidgetSize Size { get; init; }
        /// <summary>null follows the tray printer.</summary>
        public string? Serial { get; init; }
        /// <summary>Top-left corner in screen pixels; null until placed.</summary>
        public int? X { get; init; }
        public int? Y { get; init; }
        public bool OnTop { get; init; }
    }

    private readonly List<WidgetWindow> _windows = [];

    public event Action? Changed;

    public IReadOnlyList<Placement> Placements => _windows.Select(w => w.Placement).ToList();

    /// <summary>Puts back the widgets from last time.</summary>
    public void Restore()
    {
        var saved = SettingsStore.Shared.Get<List<Placement>>(SettingsKey) ?? [];
        foreach (var placement in saved) Open(placement);
    }

    public void Add(WidgetSize size, string? serial)
    {
        // A new widget goes top right of the main screen, below any already there.
        var work = SystemParameters.WorkArea;
        var dpi = Native.DpiAt(0, 0) / 96.0;
        var frame = size.FrameSize();
        var offset = _windows.Count % 6 * 28;
        var placement = new Placement
        {
            Size = size, Serial = serial,
            X = (int)((work.Right - frame.Width - 40 - offset) * dpi),
            Y = (int)((work.Top + 40 + offset) * dpi),
        };
        var window = Open(placement);
        window.Activate();
        Save();
    }

    private WidgetWindow Open(Placement placement)
    {
        var window = new WidgetWindow(placement);
        window.PlacementChanged += Save;
        window.Removed += () =>
        {
            _windows.Remove(window);
            window.Close();
            Save();
        };
        _windows.Add(window);
        window.Show();
        return window;
    }

    /// <summary>Hides them while the app shows a snapshot or quits.</summary>
    public void CloseAll()
    {
        foreach (var window in _windows.ToList()) window.Close();
        _windows.Clear();
    }

    private void Save()
    {
        SettingsStore.Shared.SetObject(SettingsKey, _windows.Select(w => w.Placement).ToList());
        Changed?.Invoke();
    }
}

/// <summary>One widget on the desktop. Drag it anywhere; right-click it for its printer, size and layer.</summary>
internal sealed class WidgetWindow : Window
{
    private static PrinterStore Store => PrinterStore.Shared;
    private const int WM_WINDOWPOSCHANGING = 0x0046;

    public DesktopWidgets.Placement Placement { get; private set; }
    public event Action? PlacementChanged;
    public event Action? Removed;

    private readonly Live _view;

    public WidgetWindow(DesktopWidgets.Placement placement)
    {
        Placement = placement;
        Title = "Bambu Bar widget";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = placement.OnTop;
        _view = new Live(Key, Build) { Margin = new Thickness(14) };  // room for the shadow
        Content = _view;
        SourceInitialized += (_, _) =>
        {
            var hwnd = Handle;
            var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            // Out of Alt+Tab and the taskbar.
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr((style | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW));
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            MoveToSaved();
        };
        MouseLeftButtonDown += (_, _) =>
        {
            // DragMove returns once the button is released; a click that didn't move it opens the printer, like
            // clicking a macOS widget.
            var before = Position();
            DragMove();
            var after = Position();
            if (before != after) Remember(after);
            else OpenPrinter();
        };
        MouseRightButtonUp += (_, e) => { ShowMenu(); e.Handled = true; };
        Theme.Changed += () => _view.Update();
    }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    public const string ActivePrintersSerial = "__active__";
    public const string AllPrintersSerial = "__all__";

    private bool IsFleetWidget => Placement.Serial is ActivePrintersSerial or AllPrintersSerial;

    private SharedSnapshot Snapshot() => Store.Fleet.SnapshotFor(Placement.Serial);

    /// <summary>Rebuilds when the printer's data changes, and once a minute for the projected time left; not on every
    /// tick. SavedAt changes with every read, so it's left out.</summary>
    private object Key()
    {
        var now = DateTime.UtcNow;
        if (IsFleetWidget)
        {
            var fleetKey = string.Join("|", Store.FleetSessions.Select(s => (s.Id, s.Config.Name, s.Status, s.Phase).GetHashCode()));
            return (Placement.Serial, fleetKey, now.Minute);
        }
        var snapshot = Snapshot() with { SavedAt = default };
        var state = new WidgetState(snapshot, now);
        return (snapshot, Placement.Size, state.PercentText, state.MinutesLeft, now.Minute);
    }

    private UIElement Build()
    {
        if (IsFleetWidget)
        {
            var snapshots = Store.FleetSessions.Select(Store.SnapshotOf).ToList();
            return WidgetViews.FleetFramed(snapshots, activeOnly: Placement.Serial == ActivePrintersSerial, DateTime.UtcNow);
        }
        return WidgetViews.Framed(Placement.Size, Snapshot(), DateTime.UtcNow);
    }

    private void OpenPrinter()
    {
        if (IsFleetWidget)
        {
            Shell.ShowMainWindow(Store.HasMultiplePrinters ? AppSection.Fleet : AppSection.Overview);
            return;
        }
        var serial = Snapshot().Serial;
        if (Store.Session(serial) is not null) Store.Select(serial);
        Shell.ShowMainWindow(AppSection.Overview);
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var printer = new MenuItem { Header = "Printer" };
        printer.Items.Add(Check("All Active Printers", Placement.Serial == ActivePrintersSerial, () => Change(Placement with { Serial = ActivePrintersSerial })));
        printer.Items.Add(Check("All Printers", Placement.Serial == AllPrintersSerial, () => Change(Placement with { Serial = AllPrintersSerial })));
        printer.Items.Add(Check("Follow the tray icon", Placement.Serial is null, () => Change(Placement with { Serial = null })));
        if (Store.Sessions.Count > 0)
        {
            printer.Items.Add(new Separator());
            foreach (var session in Store.Sessions)
            {
                var id = session.Id;
                printer.Items.Add(Check(session.Config.Name, Placement.Serial == id, () => Change(Placement with { Serial = id })));
            }
        }
        menu.Items.Add(printer);
        if (!IsFleetWidget)
        {
            var size = new MenuItem { Header = "Size" };
            foreach (var s in Enum.GetValues<WidgetSize>())
            {
                var chosen = s;
                size.Items.Add(Check(s.Title(), Placement.Size == s, () => Change(Placement with { Size = chosen })));
            }
            menu.Items.Add(size);
        }
        menu.Items.Add(Check("Keep Above Other Windows", Placement.OnTop, () =>
        {
            Change(Placement with { OnTop = !Placement.OnTop });
            Topmost = Placement.OnTop;
            if (!Placement.OnTop) SendToBottom();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open Bambu Bar", OpenPrinter));
        menu.Items.Add(Item("Remove Widget", () => Removed?.Invoke()));
        menu.IsOpen = true;
    }

    private static MenuItem Item(string title, Action action)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Check(string title, bool on, Action action)
    {
        var item = Item(title, action);
        item.IsCheckable = true;
        item.IsChecked = on;
        return item;
    }

    private void Change(DesktopWidgets.Placement placement)
    {
        Placement = placement;
        _view.Update();
        PlacementChanged?.Invoke();
    }

    private (int X, int Y) Position() => Native.GetWindowRect(Handle, out var r) ? (r.Left, r.Top) : (0, 0);

    private void Remember((int X, int Y) at) => Change(Placement with { X = at.X, Y = at.Y });

    /// <summary>Back where it was, or onto the main screen if that place is gone (a monitor unplugged).</summary>
    private void MoveToSaved()
    {
        if (Placement.X is not int x || Placement.Y is not int y) return;
        var work = Native.WorkAreaAt(x + 20, y + 20);
        var visible = x + 40 > work.Left && x < work.Right - 40 && y + 20 > work.Top && y < work.Bottom - 40;
        if (!visible) return;
        Native.SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        if (!Placement.OnTop) SendToBottom();
    }

    private void SendToBottom() =>
        Native.SetWindowPos(Handle, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);

    /// <summary>Keeps a desktop widget under other windows even when it's clicked.</summary>
    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_WINDOWPOSCHANGING && !Placement.OnTop)
        {
            var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & Native.SWP_NOZORDER) == 0)
            {
                pos.hwndInsertAfter = Native.HWND_BOTTOM;
                System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy, flags;
    }
}
