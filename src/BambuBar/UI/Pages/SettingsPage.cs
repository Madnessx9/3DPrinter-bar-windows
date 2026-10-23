using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BambuBar.App;
using BambuBar.Models;
using BambuBar.Platform;
using BambuBar.Protocol;
using BambuBar.Services;
using static BambuBar.UI.PanelViews;
using static BambuBar.UI.Pages.PageKit;
using static BambuBar.UI.Ui;

namespace BambuBar.UI.Pages;

public sealed class SettingsPage : ContentControl
{
    private static PrinterStore Store => PrinterStore.Shared;
    private static UpdateChecker Updates => UpdateChecker.Shared;

    private readonly TextBox _name = new() { Width = 220, Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _host = new() { Width = 220, Padding = new Thickness(6, 3, 6, 3) };
    private readonly PasswordBox _code = new() { Width = 220, Padding = new Thickness(6, 3, 6, 3) };
    private bool _editingConnection;
    private PrinterConfig? _shownConfig;

    public SettingsPage()
    {
        Focusable = false;
        _name.TextChanged += (_, _) => Live.Invalidate();
        _name.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && Store.Config is { } config) { Store.Rename(config.Serial, _name.Text); e.Handled = true; }
        };
        _host.TextChanged += (_, _) => Live.Invalidate();
        _code.PasswordChanged += (_, _) => Live.Invalidate();
        _code.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { ApplyConnection(); e.Handled = true; } };
        Content = new Live(Key, Build);
    }

    private object Key() => (
        string.Join("|", Store.Sessions.Select(s => (s.Id, s.Config, s.ShortState, s.IsOnline, s.Phase).GetHashCode())),
        Store.SelectedSerial, Store.MenuBarSerial, Store.MenuBarSession?.Id, Store.MenuBarStyle, Store.LinkSummary,
        Store.NotifyFinished, Store.NotifyFailed, Store.NotifyPaused, Store.NotifyStarted, Store.ConfirmStop,
        LoginItem.Shared.IsEnabled, LoginItem.Shared.DisabledBySystem, LoginItem.Shared.Error,
        Updates.State, Updates.LastChecked is { } last ? Format.Relative(last) : null, Updates.ChecksAutomatically,
        _editingConnection, _name.Text, _host.Text.Length == 0, _code.Password.Length >= 4,
        Store.Config is { } c ? Store.AnnouncedName(c.Serial) : null);

    private UIElement Build()
    {
        // Another printer chosen (or renamed elsewhere): show its name.
        if (Store.Config != _shownConfig)
        {
            if (Store.Config?.Serial != _shownConfig?.Serial) _editingConnection = false;
            _shownConfig = Store.Config;
            _name.Text = Store.Config?.Name ?? "";
        }
        var groups = new List<UIElement?> { PageHeader("Settings") };
        groups.Add(Printers());
        if (Store.Config is { } config) groups.Add(Printer(config));
        groups.Add(TrayGroup());
        groups.Add(Group("Notifications", Icon.Bell,
            Toggle("Print finished", Store.NotifyFinished, v => Store.NotifyFinished = v),
            Toggle("Print failed, canceled or an error", Store.NotifyFailed, v => Store.NotifyFailed = v),
            Toggle("Print paused (filament runout, user)", Store.NotifyPaused, v => Store.NotifyPaused = v),
            Toggle("Print started", Store.NotifyStarted, v => Store.NotifyStarted = v),
            Note("Notifications appear in Windows' notification center; click one to show that printer. Focus assist and Do Not Disturb can hold them back.")));
        groups.Add(Group("Controls", Icon.Sliders,
            Toggle("Ask before stopping a print", Store.ConfirmStop, v => Store.ConfirmStop = v),
            Rich("Pause, stop and speed changes need **LAN Only** mode with **Developer Mode** on the printer (Settings → Network on its screen). Reading status and the chamber light work in any mode.", 11.5, Theme.Secondary)));
        groups.Add(UpdatesGroup());
        groups.Add(Group("Desktop widgets", Icon.Widget,
            Row(12, Grow(Note("See what each widget size shows, put your printers on the desktop, and choose where they sit.")),
                Push("Show Widgets", () => Navigation.Shared.Section = AppSection.Widgets))));
        return Column(22, [.. groups]);
    }

    private static FrameworkElement Printers()
    {
        var content = new List<UIElement?>();
        foreach (var session in Store.Sessions)
        {
            content.Add(PrinterRow(session));
            content.Add(Divider());
        }
        content.Add(Row(12, Grow(Note(Store.IsConfigured ? "Bambu Bar watches all of them at once." : "No printer set up yet.")),
            Push("Add Printer…", Shell.AddPrinter)));
        return Group("Printers", Icon.Printer, [.. content]);
    }

    private static FrameworkElement PrinterRow(PrinterSession session)
    {
        var isCurrent = session.Id == Store.Current?.Id;
        var title = Row(6, Text(session.Config.Name, 12.5, Semibold),
            Store.HasMultiplePrinters && session.Id == Store.MenuBarSession?.Id ? Icons.Make(Icon.MenuBar, 10, Theme.Secondary).Tip("Shown in the tray") : null);
        title.HorizontalAlignment = HorizontalAlignment.Left;
        Button? show = null;
        if (Store.HasMultiplePrinters)
        {
            var id = session.Id;
            show = Push(isCurrent ? "Showing" : "Show", () => Store.Select(id), small: true);
            show.IsEnabled = !isCurrent;
        }
        return Row(12,
            new PrinterArtView(PrinterArt.For(session.Status, session.Config.ModelName, dimmed: !session.IsOnline, animated: false), 28, 34),
            Grow(Column(2, title, Text($"Bambu Lab {session.Config.ModelName} · {session.Config.Host}", 11, color: Theme.Secondary))),
            Row(5, SessionDot(session), Text(session.ShortState, 11.5, color: Theme.Secondary)),
            show);
    }

    private FrameworkElement Printer(PrinterConfig config)
    {
        var rows = new List<UIElement?>();
        var nameControls = new List<UIElement?>();
        if (_name.Text != config.Name)
        {
            nameControls.Add(Push("Revert", () => _name.Text = config.Name, small: true));
            var save = Push("Save", () => Store.Rename(config.Serial, _name.Text), accent: true, small: true);
            save.IsEnabled = _name.Text.Trim().Length > 0;
            nameControls.Add(save);
        }
        nameControls.Add(Reuse.Place(_name));
        rows.Add(SettingRow("Name", Row(8, [.. nameControls])));
        if (Store.AnnouncedName(config.Serial) is { } announced && announced != config.Name)
            rows.Add(Row(12, Grow(Note($"The printer calls itself “{announced}”. This name is only used in Bambu Bar.")),
                Push($"Use “{announced}”", () => Store.Rename(config.Serial, announced), small: true)));
        rows.Add(SettingRow("Model", Text($"Bambu Lab {config.ModelName}", 12.5, color: Theme.Secondary)));
        rows.Add(SettingRow("Serial number", Selectable(config.Serial)));
        rows.Add(SettingRow("Status", Row(6, Dot(Store.IsOnline ? Palette.Accent : Palette.Paused, 7), Text(Store.LinkSummary, 12.5, color: Theme.Secondary))));
        if (_editingConnection)
        {
            rows.Add(SettingRow("IP address", Reuse.Place(_host)));
            rows.Add(SettingRow("Access code", Reuse.Place(_code)));
            var reconnect = Push("Reconnect", ApplyConnection, accent: true);
            reconnect.IsEnabled = _host.Text.Length > 0 && _code.Password.Length >= 4;
            rows.Add(Row(8, Spacer(), Push("Cancel", () => { _editingConnection = false; Live.Invalidate(); }), reconnect));
        }
        else
        {
            rows.Add(SettingRow("IP address", Row(10, Selectable(config.Host), Push("Change…", () =>
            {
                _host.Text = config.Host;
                _code.Password = Store.AccessCode ?? "";
                _editingConnection = true;
                Live.Invalidate();
            }, small: true))));
        }
        rows.Add(Divider());
        var forget = Push("Forget Printer…", () => ConfirmForget(config));
        forget.Foreground = Theme.Brush(Palette.Failed);
        rows.Add(Row(12, Grow(Note("Bambu Bar follows the printer if its IP address changes.")), forget));
        return Group(Store.HasMultiplePrinters ? config.Name : "Printer", Icon.Printer, [.. rows]);
    }

    private void ConfirmForget(PrinterConfig config)
    {
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            "Bambu Bar disconnects and removes the access code from Windows Credential Manager. Print history stays.",
            $"Forget {config.Name}?", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK) Store.Forget(config.Serial);
    }

    private void ApplyConnection()
    {
        if (Store.Config is not { } config || _host.Text.Trim().Length == 0 || _code.Password.Length < 4) return;
        Store.Connect(config with { Host = _host.Text.Trim() }, _code.Password);
        _editingConnection = false;
        _code.Password = "";
        Live.Invalidate();
    }

    private static FrameworkElement TrayGroup()
    {
        var rows = new List<UIElement?>();
        if (Store.HasMultiplePrinters)
        {
            var picker = new ComboBox { Width = 220 };
            picker.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = null });
            picker.Items.Add(new Separator());
            foreach (var session in Store.Sessions) picker.Items.Add(new ComboBoxItem { Content = session.Config.Name, Tag = session.Id });
            picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == Store.MenuBarSerial)
                ?? picker.Items[0];
            picker.SelectionChanged += (_, _) => { if (picker.SelectedItem is ComboBoxItem item) Store.MenuBarSerial = (string?)item.Tag; };
            rows.Add(SettingRow("Printer in the tray", picker));
            if (Store.MenuBarSerial is null)
                rows.Add(Note("Automatic shows whichever printer is busy (paused first, then printing), otherwise the one you last looked at."));
        }
        var style = new ComboBox { MinWidth = 160 };
        foreach (var s in MenuBarStyleExtensions.All) style.Items.Add(new ComboBoxItem { Content = s.Title(), Tag = s });
        style.SelectedIndex = Array.IndexOf(MenuBarStyleExtensions.All, Store.MenuBarStyle);
        style.SelectionChanged += (_, _) => { if (style.SelectedItem is ComboBoxItem { Tag: MenuBarStyle chosen }) Store.MenuBarStyle = chosen; };
        rows.Add(SettingRow("While printing, show", style));
        rows.Add(Note(Store.MenuBarStyle switch
        {
            MenuBarStyle.Icon => "The tray icon shows the printer, with the part rising inside it as it prints.",
            MenuBarStyle.Percent => "The tray icon shows the percentage, with a bar of the progress under it.",
            MenuBarStyle.Remaining => "The tray icon shows the time left in hours or minutes, with a bar of the progress under it.",
            _ => "The tray icon shows the printer filling as it prints; hover it for the percentage and time left.",
        }));
        var login = LoginItem.Shared;
        rows.Add(Toggle("Open at sign-in", login.IsEnabled, login.SetEnabled));
        if (login.DisabledBySystem)
            rows.Add(Row(12, Grow(Note("Bambu Bar is turned off in Settings → Apps → Startup, so Windows won't start it.")),
                Push("Open Startup Apps", LoginItem.OpenStartupSettings, small: true)));
        if (login.Error is { } error) rows.Add(Text(error, 11.5, color: Theme.Brush(Palette.Failed), wrap: true));
        rows.Add(Note("At sign-in Bambu Bar starts in the notification area only, without a window. Click the tray icon, or run Bambu Bar again, to see this window. If the icon is hidden, drag it out of the overflow (^) onto the taskbar, or turn it on in Settings → Personalization → Taskbar."));
        return Group("Tray icon", Icon.MenuBar, [.. rows]);
    }

    private static FrameworkElement UpdatesGroup()
    {
        var state = Updates.State;
        var (icon, tint) = state switch
        {
            UpdateChecker.CheckState.Available => (Icon.Download, Theme.Brush(Palette.Accent)),
            UpdateChecker.CheckState.Failed => (Icon.Warning, Theme.Brush(Palette.Paused)),
            UpdateChecker.CheckState.UpToDate => (Icon.CheckCircle, Theme.Brush(Palette.Accent)),
            _ => (Icon.Clock, Theme.Secondary),
        };
        var title = state switch
        {
            UpdateChecker.CheckState.Available a => $"Version {a.Release.Version} is available",
            UpdateChecker.CheckState.UpToDate => "Bambu Bar is up to date",
            UpdateChecker.CheckState.Failed => "Couldn’t check for updates",
            UpdateChecker.CheckState.Checking => "Checking…",
            _ => $"Version {Updates.CurrentVersion}",
        };
        string detail;
        if (state is UpdateChecker.CheckState.Failed f) detail = f.Message;
        else if (state is UpdateChecker.CheckState.Available) detail = $"You have {Updates.CurrentVersion}. Download the new BambuBar.exe and replace this one; settings and printers stay.";
        else if (Updates.LastChecked is { } last) detail = $"Version {Updates.CurrentVersion} · last checked {Format.Relative(last)}";
        else detail = "Not checked yet";
        UIElement action = Updates.Available is { } release
            ? Push("Download", () => Open(release.PageUrl), accent: true)
            : state is UpdateChecker.CheckState.Checking ? new Spinner(14) : Push("Check Now", () => _ = Updates.Check());
        return Group("Updates", Icon.Download,
            Toggle("Check for updates automatically", Updates.ChecksAutomatically, v => Updates.ChecksAutomatically = v),
            Divider(),
            Row(10, Icons.Make(icon, 13, tint), Grow(Column(2, Text(title, 12.5, Medium), Text(detail, 11.5, color: Theme.Secondary, wrap: true))), action),
            Note($"Bambu Bar reads the latest release of {AppInfo.Repository} on GitHub once a day and never installs anything by itself."));
    }

    // MARK: - Building blocks

    private static FrameworkElement Group(string title, Icon icon, params UIElement?[] content) => Column(8,
        Label(icon, title, 13, Semibold, Theme.Primary, Theme.Primary),
        Panel(Column(12, content), 16, 16));

    private static FrameworkElement SettingRow(string title, UIElement content) =>
        Row(12, Text(title, 12.5), Spacer(12), content);

    private static FrameworkElement Toggle(string title, bool on, Action<bool> changed) =>
        Row(12, Grow(Text(title, 12.5)), new Switch(on, changed));

    private static TextBlock Note(string text) => Text(text, 11.5, color: Theme.Secondary, wrap: true);

    /// <summary>Text that can be selected and copied, like SwiftUI's textSelection.</summary>
    private static FrameworkElement Selectable(string text) => new TextBox
    {
        Text = text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
        Foreground = Theme.Secondary, FontSize = 12.5, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
    };
}
