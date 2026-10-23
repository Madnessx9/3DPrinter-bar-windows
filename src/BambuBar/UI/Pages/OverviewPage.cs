using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BambuBar.Models;
using BambuBar.Protocol;
using BambuBar.Services;
using static BambuBar.UI.Components;
using static BambuBar.UI.PanelViews;
using static BambuBar.UI.Pages.PageKit;
using static BambuBar.UI.Ui;

namespace BambuBar.UI.Pages;

/// <summary>The window's main page: the job up front, then temperatures, filament, machine details and issues.</summary>
public sealed class OverviewPage : ContentControl
{
    private static PrinterStore Store => PrinterStore.Shared;

    private readonly PrinterModelView _model = new();
    private readonly RefreshControl _refresh = new(compact: false);
    private readonly PrinterNameField _name = new();

    public OverviewPage()
    {
        Focusable = false;
        Content = new Live(() => (Store.Current?.Id, Store.Status, Store.Phase, Store.Config, Store.ChamberLightOn, Store.Speed, Store.Toast, _name.Key),
            Build);
    }

    private UIElement Build()
    {
        var rows = new List<UIElement?>
        {
            PageHeader(Reuse.Place(_name.Element()), Subtitle(), Reuse.Place(_refresh)),
        };
        if (Store.Status is { } status && Store.IsOnline)
        {
            rows.Add(JobHero(status, compact: false, _model));
            if (status.State.IsActive())
            {
                var controls = PrintControls(status);
                controls.MaxWidth = 520;
                controls.HorizontalAlignment = HorizontalAlignment.Left;
                rows.Add(controls);
                if (status.ControlsLocked) rows.Add(LockedNote());
            }
            foreach (var issue in status.Issues) rows.Add(IssueBanner(issue));
            rows.Add(Metrics(status));
            if (status.Ams.Count > 0 || status.ExternalSpool?.IsLoaded == true) rows.Add(Section("Filament", FilamentStrip(status)));
            rows.Add(Machine(status));
        }
        else
        {
            rows.Add(ConnectionCard());
        }
        if (Store.Toast is { } toast) rows.Add(ToastView(toast));
        return Column(18, [.. rows]);
    }

    private static string Subtitle()
    {
        if (Store.Config is not { } config) return "";
        List<string> parts = [$"Bambu Lab {config.ModelName}", config.Host];
        if (Store.Status?.Firmware is { } fw) parts.Add($"Firmware {fw}");
        return string.Join(" · ", parts);
    }

    private static FrameworkElement LockedNote()
    {
        var note = Row(8,
            Icons.Make(Icon.Lock, 12, Theme.Secondary).Aligned(vertical: VerticalAlignment.Top),
            Grow(Rich("The printer only accepts pause, stop and speed changes from apps in **LAN Only** mode with **Developer Mode** on (printer screen → Settings → Network). Status and the chamber light work either way.", 11.5, Theme.Secondary)));
        note.MaxWidth = 620;
        note.HorizontalAlignment = HorizontalAlignment.Left;
        return note;
    }

    private static FrameworkElement Metrics(PrinterStatus s)
    {
        var title = s.Chamber is null && Store.Config?.Model?.IsEnclosed == false ? "Temperatures & cooling" : "Temperatures & airflow";
        var tiles = TemperatureTiles(s, Store.Config?.Model);
        tiles.Add(FanTile(s));
        return Section(title, Row(10, [.. tiles.Select(t => Grow(t))]));
    }

    private static FrameworkElement Machine(PrinterStatus s)
    {
        var left = new List<UIElement?>
        {
            DetailRow("Speed", $"{Store.Speed.Title()} ({Store.Speed.Percent()}%)", Icon.Speed),
            DetailRow("Nozzle", NozzleText(s), Icon.Nozzle),
        };
        if (Store.Config?.Model?.IsEnclosed ?? true)
            left.Add(DetailRow("Door", s.DoorOpen is bool open ? open ? "Open" : "Closed" : "—", Icon.Door));
        if (s.ChamberLightOn is not null) left.Add(DetailRow("Light", Store.ChamberLightOn ? "On" : "Off", Icon.Lightbulb));
        var right = new UIElement?[]
        {
            DetailRow("Network", s.WifiSignal is int w ? $"{WiFi.Describe(w)} ({w} dBm)" : "—", Icon.Wifi),
            DetailRow("Storage", s.HasSdCard == true ? "SD card inserted" : s.HasSdCard == false ? "No SD card" : "—", Icon.SdCard),
            DetailRow("Live View", s.LiveViewEnabled == true ? "On" : s.LiveViewEnabled == false ? "Off" : "—", Icon.Video),
            DetailRow("Firmware", (s.Firmware ?? "—") + (s.UpdateAvailable ? " · Update available" : ""), Icon.Chip),
        };
        var details = Row(10, Grow(Panel(Column(9, [.. left]), 16, 13)), Grow(Panel(Column(9, right), 16, 13)));
        details.VerticalAlignment = VerticalAlignment.Top;
        foreach (UIElement child in details.Children) ((FrameworkElement)child).VerticalAlignment = VerticalAlignment.Top;
        return Section("Printer", details, Store.Config?.Model is { } model ? ModelCard(model) : null);
    }

    private static string NozzleText(PrinterStatus s)
    {
        List<string> parts = [];
        if (s.NozzleDiameter is double d) parts.Add(string.Format(CultureInfo.CurrentCulture, "{0:0.0} mm", d));
        if (s.NozzleType is { } t) parts.Add(NozzleType.Describe(t));
        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    /// <summary>What this model is built for, from Bambu Lab's published profiles.</summary>
    public static FrameworkElement ModelCard(PrinterModel model)
    {
        List<string> fans = ["Part cooling"];
        if (model.HasAuxFan) fans.Add("aux");
        if (model.HasChamberFan) fans.Add("chamber");
        var left = Column(9,
            DetailRow("Build volume", model.BuildVolumeText, Icon.Cube),
            DetailRow("Chamber", model.ChamberText, Icon.Layers),
            DetailRow("Nozzles", model.NozzleCount == 2 ? $"Two, up to {model.NozzleMax} °C" : $"One, up to {model.NozzleMax} °C", Icon.Flame));
        var right = Column(9,
            DetailRow("Bed", $"Up to {model.BedMax} °C", Icon.Heat),
            DetailRow("Fans", string.Join(", ", fans), Icon.Fan),
            DetailRow("Filament", model.UsesAmsLite ? "AMS Lite" : "AMS, AMS 2 Pro, AMS HT", Icon.Spool));
        var a = Panel(left, 16, 13);
        var b = Panel(right, 16, 13);
        a.VerticalAlignment = b.VerticalAlignment = VerticalAlignment.Top;
        return Row(10, Grow(a), Grow(b));
    }
}

/// <summary>The printer's name as the page title, with a pencil to rename it. The name is Bambu Bar's own: it starts
/// as the one the printer announces (set in Bambu Handy) and changing it here doesn't rename the printer.</summary>
public sealed class PrinterNameField
{
    private static PrinterStore Store => PrinterStore.Shared;

    private readonly TextBox _field = new()
    {
        FontSize = 24, FontWeight = FontWeights.Bold, MaxWidth = 420, MinWidth = 220, Padding = new Thickness(6, 1, 6, 1),
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _hint = Text("", 11.5, color: Theme.Secondary, wrap: true);
    private Button? _save;
    private bool _editing;
    private string? _serial;

    public PrinterNameField()
    {
        _field.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { Save(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Escape) { Stop(); e.Handled = true; }
        };
        // The hint and Save follow the draft in place: rebuilding would move the field and drop its focus.
        _field.TextChanged += (_, _) => Refresh();
    }

    /// <summary>What the page's key needs to see.</summary>
    public object Key => _editing;

    private void Refresh()
    {
        if (_save is not null) _save.IsEnabled = _field.Text.Trim().Length > 0;
        if (Store.Config is { } config) _hint.Text = Hint(config);
    }

    public FrameworkElement Element()
    {
        // Another printer chosen while editing: start over.
        if (_editing && _serial != Store.Config?.Serial) _editing = false;
        if (_editing && Store.Config is { } config)
        {
            _save = Push("Save", Save, accent: true);
            _field.FontFamily = Theme.DisplayFont;
            _hint.Foreground = Theme.Secondary;
            Refresh();
            return Column(6,
                Row(8, Reuse.Place(_field), Push("Cancel", Stop), _save).Aligned(HorizontalAlignment.Left),
                Reuse.Place(_hint));
        }
        var title = Text(Store.PrinterName, 26, Bold, display: true);
        if (Store.Config is null) return title;
        var pencil = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Theme.Fill(0.07),
            Child = Icons.Make(Icon.Pencil, 12, Theme.Secondary),
        };
        var edit = Button(pencil, Begin, t => pencil.Background = Theme.Fill(t.IsMouseOver ? 0.11 : 0.07), "Rename this printer in Bambu Bar");
        return Row(8, title, edit).Aligned(HorizontalAlignment.Left);
    }

    private void Begin()
    {
        _field.Text = Store.PrinterName;
        _serial = Store.Config?.Serial;
        _editing = true;
        Live.Invalidate();
        _field.Dispatcher.BeginInvoke(() => { _field.Focus(); _field.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Stop()
    {
        _editing = false;
        Live.Invalidate();
    }

    private string Hint(PrinterConfig config)
    {
        if (Store.AnnouncedName(config.Serial) is { } announced && announced != _field.Text)
            return $"Only in Bambu Bar. The printer still calls itself “{announced}”; rename it there in Bambu Handy.";
        return "Only in Bambu Bar, for the tray, window and widgets. The printer keeps its own name.";
    }

    private void Save()
    {
        if (Store.Config?.Serial is not { } serial || _field.Text.Trim().Length == 0) return;
        Store.Rename(serial, _field.Text);
        Stop();
    }
}
