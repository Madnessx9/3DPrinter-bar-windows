using System.Windows;
using System.Windows.Threading;
using BambuBar.Models;
using BambuBar.Platform;
using BambuBar.Protocol;
using BambuBar.Services;
using BambuBar.UI;
using BambuBar.UI.Widgets;
using Microsoft.Win32;

namespace BambuBar.App;

/// <summary>The running app: the store, the tray icon and its panel, the main window, notifications, widgets.</summary>
internal sealed class AppHost : IShell
{
    private static PrinterStore Store => PrinterStore.Shared;

    private readonly Application _app;
    private readonly SingleInstance? _instance;
    private TrayIcon? _tray;
    private FlyoutWindow? _flyout;
    private MainWindow? _main;
    private MainTimer? _clock;
    private TrayIconState? _shownIcon;
    private int _shownIconSize;
    private bool _quitting;

    public AppHost(Application app, SingleInstance? instance)
    {
        _app = app;
        _instance = instance;
    }

    public void Start()
    {
        var args = AppArguments.Current;
        var ephemeral = args.IsEphemeral;
        SettingsStore.Shared = SettingsStore.ForApp(ephemeral);
        UpdateChecker.Shared = new UpdateChecker(SettingsStore.Shared);
        var notifier = new ToastNotifier();
        ICredentialStore credentials = ephemeral ? new MemoryCredentialStore() : new CredentialStore();
        PrinterStore.Shared = new PrinterStore(SettingsStore.Shared, credentials, ephemeral && args.Has("--snapshot") ? new NullNotifier() : notifier,
            HistoryStore.ForApp(ephemeral));
        Shell.Current = this;

        Store.ConfirmStopPrompt = (title, message) =>
        {
            var owner = _main is { IsVisible: true } m ? m : null;
            var answer = owner is null
                ? MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
                : MessageBox.Show(owner, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            return answer == MessageBoxResult.OK;
        };
        notifier.Activated += serial =>
        {
            if (serial is not null && Store.Session(serial) is not null) Store.Select(serial);
            ShowMainWindow(AppSection.Overview);
        };

        Store.Changed += OnStoreChanged;
        UpdateChecker.Shared.Changed += Live.Invalidate;
        Theme.Changed += OnThemeChanged;
        LoginItem.Shared.Changed += Live.Invalidate;
        // Relative times ("3s ago"), projected widget progress and the tray's tooltip move on their own.
        _clock = MainTimer.Every(TimeSpan.FromSeconds(1), Live.Invalidate);

        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Suspend) MainThread.Post(PowerEvents.RaiseWillSleep);
            else if (e.Mode == PowerModes.Resume) MainThread.Post(PowerEvents.RaiseDidWake);
        };
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color) MainThread.Post(Theme.Read);
        };
        if (_instance is not null)
        {
            _instance.Received += received => MainThread.Post(() => OnLaunchedAgain(received));
            _instance.Listen();
        }

        ApplyTheme();
        Store.Start();
        // Load the printer model off the UI thread, so the first panel opens straight away.
        foreach (var session in Store.Sessions.Take(4)) ModelLibrary.Preload(PrinterShapes.FromModel(session.Config.ModelName));
        if (Store.Sessions.Count == 0) ModelLibrary.Preload(PrinterShapes.FromModel("P2S"));

        if (args.Value("--snapshot") is { } dir)
        {
            Snapshots.Run(dir, this);
            return;
        }

        _tray = new TrayIcon();
        _tray.Activated += () => Flyout.Toggle(_tray.Bounds());
        _tray.ContextMenuRequested += ShowTrayMenu;
        _tray.RedrawNeeded += () => UpdateTrayIcon(force: true);
        _tray.ThemeChanged += Theme.Read;
        UpdateTrayIcon(force: true);

        UpdateChecker.Shared.StartAutomaticChecks();
        LoginItem.Shared.Refresh();
        if (!args.IsEphemeral) DesktopWidgets.Shared.Restore();
        if (!args.Background) ShowMainWindow();
    }

    private FlyoutWindow Flyout => _flyout ??= new FlyoutWindow();

    // MARK: - IShell

    public void ShowMainWindow(AppSection? section = null, DiscoveredPrinter? setup = null)
    {
        _flyout?.HidePanel();
        _main ??= new MainWindow();
        _main.ShowAndActivate();
    }

    public void HideFlyout() => _flyout?.HidePanel();

    public void AddWidget(WidgetSize size, string? serial) => DesktopWidgets.Shared.Add(size, serial);

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _clock?.Dispose();
        Store.Stop();
        DesktopWidgets.Shared.CloseAll();
        _flyout?.Close();
        if (_main is not null)
        {
            _main.Quitting = true;
            _main.Close();
        }
        _tray?.Dispose();
        _app.Shutdown();
    }

    // MARK: - Events

    private void OnStoreChanged()
    {
        Live.Invalidate();
        UpdateTrayIcon();
    }

    private void OnThemeChanged()
    {
        ApplyTheme();
        Live.Invalidate();
        UpdateTrayIcon(force: true);
    }

    private void ApplyTheme()
    {
        // Fluent styles for the standard controls (text fields, buttons, menus, scroll bars), in the app's mode.
        _app.ThemeMode = Theme.Dark ? ThemeMode.Dark : ThemeMode.Light;
        Native.FollowDarkModeInMenus(Theme.Dark);
    }

    /// <summary>Opened again from the Start menu, a shortcut or a toast while already running: show the window.</summary>
    private void OnLaunchedAgain(string[] args)
    {
        if (args.Contains("--background", StringComparer.OrdinalIgnoreCase)) return;
        ShowMainWindow();
    }

    // MARK: - Tray icon

    private void UpdateTrayIcon(bool force = false)
    {
        if (_tray is null) return;
        var state = TrayIconState.Current();
        var size = Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, Native.TaskbarDpi());
        if (size <= 0) size = 16;
        if (!force && state == _shownIcon && size == _shownIconSize) return;
        // Same picture, new tooltip: no need to draw.
        _shownIcon = state;
        _shownIconSize = size;
        _tray.Update(TrayIconArt.Icon(state, size), state.Tooltip);
    }

    private enum MenuCommand { Open = 1, Settings, Refresh, Pause, Resume, Stop, Light, Updates, Widgets, Quit, PrinterBase = 100 }

    /// <summary>The right-click menu: the app's commands, which macOS puts in its menu bar.</summary>
    private void ShowTrayMenu(int x, int y)
    {
        _flyout?.HidePanel();
        var menu = Native.CreatePopupMenu();
        var submenus = new List<IntPtr>();
        void Add(IntPtr target, MenuCommand id, string title, bool enabled = true, bool check = false) =>
            Native.AppendMenu(target, Native.MF_STRING | (enabled ? 0 : Native.MF_GRAYED) | (check ? Native.MF_CHECKED : 0), (IntPtr)(int)id, title);
        void Separator() => Native.AppendMenu(menu, Native.MF_SEPARATOR, IntPtr.Zero, null);

        Add(menu, MenuCommand.Open, "Open Bambu Bar");
        Native.SetMenuDefaultItem(menu, (uint)MenuCommand.Open, 0);
        Separator();
        if (Store.HasMultiplePrinters)
        {
            var printers = Native.CreatePopupMenu();
            submenus.Add(printers);
            for (var i = 0; i < Store.Sessions.Count; i++)
            {
                var session = Store.Sessions[i];
                Add(printers, MenuCommand.PrinterBase + i, $"{session.Config.Name}\t{session.ShortState}", check: session.Id == Store.Current?.Id);
            }
            Native.AppendMenu(menu, Native.MF_POPUP, printers, $"Printer: {Store.PrinterName}");
        }
        var state = Store.Status?.State;
        var online = Store.IsOnline;
        Add(menu, MenuCommand.Refresh, "Refresh", Store.IsConfigured);
        Add(menu, MenuCommand.Pause, "Pause", online && state is JobState.Printing or JobState.Preparing);
        Add(menu, MenuCommand.Resume, "Resume", online && state is JobState.Paused);
        Add(menu, MenuCommand.Stop, "Stop…", online && (state?.IsActive() ?? false));
        Add(menu, MenuCommand.Light, Store.ChamberLightOn ? "Turn Chamber Light Off" : "Turn Chamber Light On", online && Store.Status?.ChamberLightOn is not null);
        Separator();
        Add(menu, MenuCommand.Widgets, "Widgets…");
        Add(menu, MenuCommand.Settings, "Settings…");
        Add(menu, MenuCommand.Updates, UpdateChecker.Shared.State is UpdateChecker.CheckState.Checking ? "Checking for Updates…" : "Check for Updates…",
            UpdateChecker.Shared.State is not UpdateChecker.CheckState.Checking);
        Separator();
        Add(menu, MenuCommand.Quit, "Quit Bambu Bar");

        // The menu only closes on an outside click if the app is in front (a documented quirk of tray menus).
        Native.SetForegroundWindow(_tray!.Handle);
        var chosen = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN, x, y, _tray.Handle, IntPtr.Zero);
        Native.PostMessage(_tray.Handle, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        Native.DestroyMenu(menu);  // destroys the submenus with it
        Run((MenuCommand)chosen);
    }

    private void Run(MenuCommand command)
    {
        switch (command)
        {
            case 0: return;
            case MenuCommand.Open: ShowMainWindow(); break;
            case MenuCommand.Settings: Shell.ShowMainWindow(AppSection.Settings); break;
            case MenuCommand.Widgets: Shell.ShowMainWindow(AppSection.Widgets); break;
            case MenuCommand.Refresh: Store.Refresh(); break;
            case MenuCommand.Pause: Store.Pause(); break;
            case MenuCommand.Resume: Store.Resume(); break;
            case MenuCommand.Stop: Store.StopPrint(); break;
            case MenuCommand.Light: Store.ToggleLight(); break;
            case MenuCommand.Updates: _ = CheckForUpdates(); break;
            case MenuCommand.Quit: Quit(); break;
            case >= MenuCommand.PrinterBase:
                var index = command - MenuCommand.PrinterBase;
                if (index < Store.Sessions.Count) Store.Select(Store.Sessions[index].Id);
                break;
        }
    }

    /// <summary>A check asked for by hand reports its result, unlike the daily one.</summary>
    private async Task CheckForUpdates()
    {
        var updates = UpdateChecker.Shared;
        await updates.Check();
        switch (updates.State)
        {
            case UpdateChecker.CheckState.Available a:
                if (MessageBox.Show($"Bambu Bar {a.Release.Version} is available. You have {updates.CurrentVersion}.\n\nOpen the download page?",
                        "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    Ui.Open(a.Release.PageUrl);
                break;
            case UpdateChecker.CheckState.UpToDate:
                MessageBox.Show($"Bambu Bar {updates.CurrentVersion} is the newest version.", "You're up to date", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            case UpdateChecker.CheckState.Failed f:
                MessageBox.Show(f.Message, "Couldn’t check for updates", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
    }
}
