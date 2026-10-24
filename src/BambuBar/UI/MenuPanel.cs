using System.Windows;
using System.Windows.Controls;
using BambuBar.App;
using BambuBar.Models;
using BambuBar.Protocol;
using BambuBar.Services;
using static BambuBar.UI.PanelViews;
using static BambuBar.UI.Ui;

namespace BambuBar.UI;

/// <summary>The panel above the tray icon: everything about the print at a glance, plus the common controls.</summary>
public sealed class MenuPanel : ContentControl
{
    private static PrinterStore Store => PrinterStore.Shared;

    private readonly PrinterModelView _model = new();
    private readonly RefreshControl _refresh = new(compact: true);
    /// <summary>Set by snapshots, which mustn't touch the setting.</summary>
    private readonly bool? _showsAllOverride;

    public MenuPanel(bool? showsAllOverride = null)
    {
        _showsAllOverride = showsAllOverride;
        Focusable = false;
        Width = 372;
        Content = new Live(Key, Build);
    }

    private bool ShowsAll
    {
        get => _showsAllOverride ?? Store.PanelShowsAll;
        set { Store.PanelShowsAll = value; Live.Invalidate(); }
    }

    private bool Fleet => ShowsAll && Store.HasMultiplePrinters;

    /// <summary>Opening the panel shows the printer the tray icon is showing.</summary>
    public void Opened()
    {
        if (_showsAllOverride is null && Store.MenuBarSession?.Id is { } serial) Store.Select(serial);
    }

    // The panel rebuilds whenever the store reports; the status record and phase carry everything it shows.
    private object Key() => (Fleet, Store.FleetActiveOnly, Store.SelectedSerial, Store.Current?.Status, Store.Phase, Store.ChamberLightOn, Store.Speed,
        Store.Toast, UpdateChecker.Shared.Offered, Store.Sessions.Count, Store.Discovered.Count, Store.Config,
        Fleet ? string.Join("|", Store.Sessions.Select(s => s.Status?.GetHashCode() ^ s.Phase.GetHashCode())) : null);

    private UIElement Build()
    {
        var rows = new List<UIElement?> { Header() };
        if (!Store.IsConfigured)
        {
            rows.Add(SetupPrompt());
        }
        else if (Fleet)
        {
            rows.Add(FleetList(session => { Store.Select(session.Id); ShowsAll = false; }));
        }
        else if (Store.Status is { } status && Store.IsOnline)
        {
            rows.Add(JobHero(status, compact: true, _model));
            if (status.MostSevereIssue is { } issue && issue.Severity <= IssueSeverity.Common) rows.Add(IssueBanner(issue));
            rows.Add(Components.TemperaturesLayout(status, Store.Config?.Model, includeFans: false, spacing: 8));
            if (status.Ams.Count > 0 || status.ExternalSpool?.IsLoaded == true) rows.Add(FilamentStrip(status));
            if (status.State.IsActive()) rows.Add(PrintControls(status));
        }
        else
        {
            rows.Add(ConnectionCard());
        }
        if (UpdateChecker.Shared.Offered is { } release) rows.Add(UpdateRow(release));
        if (Store.Toast is { } toast) rows.Add(ToastView(toast));
        rows.Add(Footer());
        return Pad(Column(10, [.. rows]), 12);
    }

    private FrameworkElement Header()
    {
        var title = new List<UIElement?>();
        if (Fleet)
        {
            title.Add(Text(Store.FleetActiveOnly ? "Active Printers" : "All Printers", 14, Semibold));
            title.Add(Text(Store.FleetSummary, 11, color: Theme.Secondary));
        }
        else
        {
            if (Store.HasMultiplePrinters)
            {
                var label = Row(5, Text(Store.PrinterName, 14, Semibold), Icons.Make(Icon.ChevronDown, 9.5, Theme.Secondary));
                Tap? picker = null;
                picker = Plain(label, () => ShowPrinterMenu(picker!), "Show another printer", 6, new Thickness(0));
                picker.HorizontalAlignment = HorizontalAlignment.Left;
                title.Add(picker);
            }
            else
            {
                title.Add(Text(Store.IsConfigured ? Store.PrinterName : "Bambu Bar", 14, Semibold));
            }
            var link = Row(5, Dot(LinkColor()), Text(LinkLine(), 11, color: Theme.Secondary));
            link.HorizontalAlignment = HorizontalAlignment.Left;
            title.Add(link);
        }
        var buttons = new List<UIElement?> { Grow(Column(1, [.. title])), Spacer(8) };
        if (Fleet)
        {
            buttons.Add(Push(Store.FleetActiveOnly ? "Active" : "All", () => Store.FleetActiveOnly = !Store.FleetActiveOnly,
                accent: Store.FleetActiveOnly, small: true));
        }
        if (Store.HasMultiplePrinters)
            buttons.Add(HeaderIcon(Icon.Grid, () => ShowsAll = !ShowsAll, ShowsAll ? "Show one printer" : "Show all printers")
                .Fade(ShowsAll ? 1 : 0.85));
        if (Store.IsConfigured) buttons.Add(Reuse.Place(_refresh));
        buttons.Add(HeaderIcon(Icon.Settings, () => Shell.ShowMainWindow(AppSection.Settings), "Settings"));
        return Row(10, [.. buttons]).WithMargin(4, 2, 4, 0);
    }

    private static System.Windows.Media.Color LinkColor() => Store.Phase switch
    {
        ConnectionPhase.Online => Palette.Accent,
        ConnectionPhase.Connecting => Palette.Paused,
        ConnectionPhase.Idle => Theme.PrimaryColor.WithOpacity(0.6),
        _ => Palette.Failed,
    };

    private static string LinkLine()
    {
        if (Store.Config is not { } config) return "No printer set up";
        List<string> parts = [config.ModelName];
        if (Store.Phase is ConnectionPhase.Online)
            parts.Add(Store.Status?.WifiSignal is { } wifi ? WiFi.Describe(wifi) : "Connected");
        else
            parts.Add(Store.LinkSummary);
        return string.Join(" · ", parts);
    }

    private static FrameworkElement Footer()
    {
        var open = Plain(Label(Icon.Window, "Open Bambu Bar", 12.5, Medium, iconSize: 12), () => Shell.ShowMainWindow(AppSection.Overview));
        var quit = Plain(Text("Quit", 12.5, Medium), Shell.Quit);
        return Row(6, open, Spacer(), quit).WithMargin(2, 0, 2, 0);
    }
}
