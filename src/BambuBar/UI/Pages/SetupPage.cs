using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BambuBar.App;
using BambuBar.Models;
using BambuBar.Platform;
using BambuBar.Protocol;
using BambuBar.Services;
using static BambuBar.UI.Pages.PageKit;
using static BambuBar.UI.Ui;

namespace BambuBar.UI.Pages;

/// <summary>
/// First run: pick the printer Bambu Bar found on the network (or type its address). The access code comes from
/// Bambu Studio or OrcaSlicer when either knows the printer (on this PC, or a settings file from another computer);
/// otherwise it's typed in from the printer's screen.
/// </summary>
public sealed class SetupPage : ContentControl
{
    private static PrinterStore Store => PrinterStore.Shared;
    private static Navigation Navigation => Navigation.Shared;

    private readonly TextBox _host = Field("192.168.1.50");
    private readonly TextBox _serial = Field("22E…");
    private readonly TextBox _name = Field("My P2S");
    private readonly PasswordBox _code = new()
    {
        FontFamily = Theme.MonoFont, FontSize = 15, MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260,
        MaxLength = 16, Padding = new Thickness(8, 5, 8, 5), ToolTip = "8-character access code",
    };
    private readonly CheckBox _openAtLogin = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
    private readonly PrinterArtView _heroArt = new(PrinterArt.For(SamplePrinter.Status()), 150, 180);

    private string _modelCode = "N7";
    private string? _selected;
    private bool _manual;
    /// <summary>The one found printer is picked automatically once; after that only the person changes the selection.</summary>
    private bool _autoPicked;
    /// <summary>Where the access code came from ("Bambu Studio"…), null when typed.</summary>
    private string? _codeSource;
    private List<string> _alternativeCodes = [];
    private string? _importNote;
    private bool _dropTargeted;
    private bool _filling;
    private IReadOnlyList<SlicerPrinter>? _seenSlicerPrinters;
    private string _seenNewPrinters = "";

    public SetupPage()
    {
        Focusable = false;
        foreach (var box in new[] { _host, _serial, _name })
        {
            box.TextChanged += (_, _) =>
            {
                if (_filling) return;
                _selected = null;
                Live.Invalidate();
            };
            box.KeyDown += OnEnter;
        }
        _code.PasswordChanged += (_, _) => Live.Invalidate();
        _code.KeyDown += OnEnter;
        Content = new Live(Key, Build);
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            Store.ReloadSlicerPrinters();
            AdoptCandidate();
        };
    }

    private object Key() => (Store.IsConfigured, Store.Phase, Store.LinkDetail, Store.IsDemo, Navigation.SetupCandidate,
        string.Join("|", Store.Discovered.Select(d => d.GetHashCode())), Store.SlicerPrinters, Store.Sessions.Count,
        _selected, _manual, _codeSource, _importNote, _dropTargeted, CanConnect, _code.Password.Length == 0);

    private bool Adding => Store.IsConfigured;

    /// <summary>Printers on the network that aren't set up yet.</summary>
    private static List<DiscoveredPrinter> NewPrinters => Store.Discovered.Where(d => Store.Session(d.Serial) is null).ToList();

    /// <summary>Printers a slicer knows (with an address) that haven't announced themselves on the network.</summary>
    private static List<SlicerPrinter> SlicerOnlyPrinters => Store.SlicerPrinters
        .Where(p => p.Host is not null && Store.Session(p.Serial) is null && !Store.Discovered.Any(d => d.Serial == p.Serial)).ToList();

    /// <summary>Reacts to what changed since the last build, like SwiftUI's onChange.</summary>
    private void Sync()
    {
        if (!ReferenceEquals(_seenSlicerPrinters, Store.SlicerPrinters))
        {
            _seenSlicerPrinters = Store.SlicerPrinters;
            FillCodeFromSlicer();
        }
        AdoptCandidate();
        var fresh = string.Join(",", NewPrinters.Select(p => p.Serial));
        if (fresh != _seenNewPrinters)
        {
            _seenNewPrinters = fresh;
            if (!_autoPicked && _host.Text.Length == 0 && NewPrinters is [var only])
            {
                _autoPicked = true;
                Pick(only);
            }
        }
    }

    private UIElement Build()
    {
        Sync();
        var rows = new List<UIElement?>();
        rows.Add(Adding
            ? PageHeader("Add a Printer", "Bambu Bar watches every printer you add; pick which one the tray icon shows in Settings.",
                Push("Cancel", () => Navigation.AddingPrinter = false))
            : Hero());

        // 1 Choose your printer
        var fresh = NewPrinters;
        var choose = new List<UIElement?>
        {
            Row(8, Grow(SectionLabel("1  Choose your printer")), fresh.Count == 0 ? new Spinner(12) : null,
                Push("Search Again", Store.SearchForPrinters, small: true)),
        };
        if (fresh.Count == 0 && !_manual)
            choose.Add(Panel(Row(10, Icons.Make(Icon.Wifi, 13, Theme.Secondary).Aligned(vertical: VerticalAlignment.Top),
                Grow(Text("Looking for printers on this network. Printers announce themselves every few seconds; make sure yours is on and on the same network as this PC. The first time, Windows may ask whether Bambu Bar can use private networks: allow it, or printers can't be found.", 12, color: Theme.Secondary, wrap: true))), 14, 12));
        if (Store.ReadySlicerPrinters is { Count: >= 2 } ready) choose.Add(AddAllCard(ready));
        foreach (var printer in fresh) choose.Add(DiscoveredRow(printer));
        foreach (var printer in SlicerOnlyPrinters)
            choose.Add(DiscoveredRow(new DiscoveredPrinter { Serial = printer.Serial, Host = printer.Host ?? "", Name = printer.Serial, LastSeen = DateTime.MinValue }));
        choose.Add(ManualEntry());
        rows.Add(Column(10, [.. choose]));

        // 2 Access code
        rows.Add(Column(10, SectionLabel("2  Access code"), _codeSource is { } source ? FoundCode(source) : CodeEntry()));

        if (!Adding)
        {
            _openAtLogin.Content = Column(1,
                Text("Open Bambu Bar when you sign in", 12.5, Medium),
                Text("It starts quietly in the notification area, without a taskbar button.", 11.5, color: Theme.Secondary));
            rows.Add(Reuse.Place(_openAtLogin));
        }

        var connecting = Store.Phase is ConnectionPhase.Connecting;
        var connect = ControlButton(connecting ? "Connecting…" : "Connect", null, Connect, Palette.AccentDeep, prominent: true);
        connect.Width = 150;
        connect.IsEnabled = CanConnect;
        connect.IsDefault = true;
        var actions = new List<UIElement?> { connect };
        if (Store.LinkDetail is { } detail)
            actions.Add(Grow(Row(6, Icons.Make(Icon.Warning, 12, Theme.Brush(Palette.Paused)), Grow(Text(detail, 12, color: Theme.Brush(Palette.Paused), wrap: true)))));
        rows.Add(Row(12, [.. actions]));
        return Column(22, [.. rows]);
    }

    private FrameworkElement Hero()
    {
        _heroArt.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.3 };
        return Row(24, Reuse.Place(_heroArt),
            Grow(Column(8,
                Text("Welcome to Bambu Bar", 28, Bold, display: true),
                Text("Your printer's progress, temperatures and filament, one click away in the notification area. Bambu Bar talks to the printer directly over your network: no account, no cloud.", 13.5, color: Theme.Secondary, wrap: true))))
            .WithMargin(0, 0, 0, 4);
    }

    /// <summary>Several printers with saved codes: add them all in one go rather than one at a time.</summary>
    private FrameworkElement AddAllCard(List<SlicerPrinter> printers)
    {
        var sources = string.Join(" and ", printers.Select(p => p.Source).Distinct().Order());
        var models = string.Join(" · ", printers
            .GroupBy(p => PrinterModel.Find(ModelCodeFor(p), p.Serial)?.Name ?? "Other")
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => $"{g.Count()} {g.Key}"));
        var add = ControlButton($"Add All {printers.Count}", null, () =>
        {
            var firstPrinter = !Adding;
            Store.AddAll(printers);
            Navigation.AddingPrinter = false;
            if (firstPrinter && _openAtLogin.IsChecked == true && !LoginItem.Shared.IsEnabled && !Store.IsDemo) LoginItem.Shared.SetEnabled(true);
        }, Palette.AccentDeep, prominent: true);
        add.Width = 120;
        return Panel(Row(12,
            Badge(Icon.Layers),
            Grow(Column(2,
                Text($"{printers.Count} printers in {sources}", 13, Semibold),
                Text(models, 11.5, color: Theme.Secondary, wrap: true),
                Text("Their access codes are saved there, so they can all be added at once.", 11.5, color: Theme.Secondary, wrap: true))),
            add), 16, 14, Palette.Accent);
    }

    private static FrameworkElement Badge(Icon icon) => new Border
    {
        Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Theme.Brush(Palette.Accent, 0.15),
        Child = Icons.Make(icon, 15, Theme.Brush(Palette.Accent)),
    };

    private static string ModelCodeFor(SlicerPrinter printer) => Store.Discovered.FirstOrDefault(d => d.Serial == printer.Serial)?.ModelCode ?? "";

    /// <summary>The code is already known: say where from, and offer to type one instead.</summary>
    private FrameworkElement FoundCode(string source) => Panel(Row(12,
        Badge(Icon.Key),
        Grow(Column(2,
            Text($"Found in {source}", 13, Semibold),
            Text("Bambu Bar uses the access code saved there for this printer. It's kept in Windows Credential Manager and only ever sent to the printer.", 11.5, color: Theme.Secondary, wrap: true))),
        Push("Type a Code Instead", () =>
        {
            _codeSource = null;
            _code.Password = "";
            _alternativeCodes = [];
            Live.Invalidate();
            Dispatcher.BeginInvoke(() => _code.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }, small: true)), 16, 14, Palette.Accent);

    private FrameworkElement CodeEntry()
    {
        var help = Rich("On the printer's screen open **Settings → Network** (the Wi-Fi/LAN page) and read the **Access Code**. It's stored in Windows Credential Manager and only ever sent to the printer.", 11.5, Theme.Secondary);
        help.MaxWidth = 440;
        help.HorizontalAlignment = HorizontalAlignment.Left;
        var import = Row(10,
            Icons.Make(Icon.Download, 14, _dropTargeted ? Theme.Brush(Palette.Accent) : Theme.Secondary),
            Grow(Column(1,
                Text("Bambu Studio on another computer?", 12, Semibold),
                Rich(_importNote ?? "Drop its **BambuStudio.conf** here, or its BambuStudio folder. On Windows it's in **%\u2060APPDATA%\u2060\\BambuStudio**; on a Mac in **Library → Application Support → BambuStudio**.", 11.5, Theme.Secondary))),
            Push("Choose File…", ChooseSettingsFile, small: true),
            Push("Choose Folder…", ChooseSettingsFolder, small: true));
        var panel = Panel(Column(12, Reuse.Place(_code), help, Divider(), import), 16, 14, _dropTargeted ? Palette.Accent : null);
        panel.AllowDrop = true;
        panel.DragEnter += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { _dropTargeted = true; Live.Invalidate(); } };
        panel.DragLeave += (_, _) => { _dropTargeted = false; Live.Invalidate(); };
        panel.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        panel.Drop += (_, e) =>
        {
            _dropTargeted = false;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) ImportSettings(paths[0]);
            Live.Invalidate();
        };
        return panel;
    }

    private void ChooseSettingsFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose Bambu Studio's settings",
            Filter = "Slicer settings (*.conf)|*.conf|All files (*.*)|*.*",
            InitialDirectory = SlicerImport.SuggestedFolder,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) ImportSettings(dialog.FileName);
    }

    private void ChooseSettingsFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the BambuStudio folder",
            InitialDirectory = SlicerImport.SuggestedFolder,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) ImportSettings(dialog.FolderName);
    }

    private void ImportSettings(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        var count = Store.ImportSlicerSettings(path);
        if (count == 0)
        {
            _importNote = $"No Bambu Studio or OrcaSlicer access codes found in {name}.";
            Live.Invalidate();
            return;
        }
        _importNote = null;
        if (_serial.Text.Length == 0 && Store.SlicerPrinters.FirstOrDefault() is { } first)
        {
            if (Store.Discovered.FirstOrDefault(d => d.Serial == first.Serial) is { } found) Pick(found);
            else if (first.Host is { } host) Pick(new DiscoveredPrinter { Serial = first.Serial, Host = host, Name = first.Serial, LastSeen = DateTime.UtcNow });
            else Set(_serial, first.Serial);
        }
        _seenSlicerPrinters = Store.SlicerPrinters;
        FillCodeFromSlicer();
        if (_codeSource is null)
            _importNote = $"{name} doesn't know {(_serial.Text.Length == 0 ? "this printer" : _serial.Text)}. Pick another printer or type the code.";
        Live.Invalidate();
    }

    /// <summary>Fills in the code a slicer has for the chosen printer.</summary>
    private void FillCodeFromSlicer()
    {
        if (_serial.Text.Length == 0 || Store.SlicerPrinter(_serial.Text) is not { } printer || printer.AccessCodes.Count == 0) return;
        if (_codeSource is null && _code.Password.Length > 0) return;  // never overwrite a typed code
        _code.Password = printer.AccessCodes[0];
        _alternativeCodes = printer.AccessCodes.Skip(1).ToList();
        _codeSource = printer.Source;
    }

    private FrameworkElement DiscoveredRow(DiscoveredPrinter printer)
    {
        var isSelected = _selected == printer.Serial;
        var known = Store.SlicerPrinter(printer.Serial);
        var chips = new List<UIElement?>();
        if (known is not null)
        {
            var chip = Row(4, Icons.Make(Icon.Key, 9.5, Theme.Brush(Palette.Accent)),
                Text(printer.LastSeen == DateTime.MinValue ? $"From {known.Source}" : $"Code in {known.Source}", 10.5, Semibold, Theme.Brush(Palette.Accent)));
            chips.Add(new Border { Child = chip, Padding = new Thickness(7, 2, 7, 2), CornerRadius = new CornerRadius(10), Background = Theme.Brush(Palette.Accent, 0.12) });
        }
        if (printer.ConnectMode is { } mode)
            chips.Add(new Border
            {
                Child = Text(mode.Equals("lan", StringComparison.OrdinalIgnoreCase) ? "LAN Only" : "Cloud", 10.5, Semibold, Theme.Secondary),
                Padding = new Thickness(7, 2, 7, 2), CornerRadius = new CornerRadius(10), Background = Theme.Fill(0.07),
            });
        var model = printer.ModelCode.Length > 0 ? $"Bambu Lab {printer.ModelName} · " : "";
        // A check when picked, an empty ring otherwise, like a radio button.
        FrameworkElement marker = isSelected
            ? Icons.Make(Icon.CheckCircle, 17, Theme.Brush(Palette.Accent))
            : new System.Windows.Shapes.Ellipse { Width = 15, Height = 15, Stroke = Theme.Secondary, StrokeThickness = 1.3, Margin = new Thickness(1) };
        var content = Row(12,
            marker,
            Grow(Column(2, Text(printer.Name, 13, Semibold), Text($"{model}{printer.Host} · {printer.Serial}", 11.5, color: Theme.Secondary))),
            Row(6, [.. chips]));
        var panel = Panel(content, 14, 12, isSelected ? Palette.Accent : null);
        return Button(panel, () => Pick(printer), t => panel.Opacity = t.IsPressed ? 0.85 : 1);
    }

    private FrameworkElement ManualEntry()
    {
        var chevron = Icons.Make(_manual ? Icon.ChevronDown : Icon.ChevronRight, 10, Theme.Secondary).Sized(14);
        var toggle = Plain(Row(6, chevron, Text("Enter address manually", 12, Medium)), () => { _manual = !_manual; Live.Invalidate(); },
            padding: new Thickness(2, 4, 6, 4));
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        if (!_manual) return toggle;
        return Column(10, toggle,
            Column(10, FieldRow("IP address", _host), FieldRow("Serial number", _serial), FieldRow("Name", _name)).WithMargin(0, 0, 0, 0));
    }

    private static FrameworkElement FieldRow(string title, TextBox box) =>
        Row(8, Text(title, 12, color: Theme.Secondary).Sized(110), Reuse.Place(box));

    private static TextBox Field(string placeholder) => new()
    {
        MinWidth = 280, MaxWidth = 280, Padding = new Thickness(6, 4, 6, 4), ToolTip = placeholder, HorizontalAlignment = HorizontalAlignment.Left,
    };

    private void Set(TextBox box, string text)
    {
        _filling = true;
        box.Text = text;
        _filling = false;
    }

    private void Pick(DiscoveredPrinter printer)
    {
        Set(_host, printer.Host);
        Set(_serial, printer.Serial);
        Set(_name, printer.Name);
        _modelCode = printer.ModelCode;
        if (_codeSource is not null)
        {
            // A code that came with another printer doesn't belong to this one.
            _code.Password = "";
            _codeSource = null;
            _alternativeCodes = [];
        }
        FillCodeFromSlicer();
        _selected = printer.Serial;
        Live.Invalidate();
        if (_codeSource is null) Dispatcher.BeginInvoke(() => _code.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void AdoptCandidate()
    {
        if (Navigation.SetupCandidate is { } candidate)
        {
            Navigation.SetupCandidate = null;
            Pick(candidate);
        }
        else if (!_autoPicked && _host.Text.Length == 0 && NewPrinters is [var only])
        {
            _autoPicked = true;
            Pick(only);
        }
    }

    private bool CanConnect =>
        _host.Text.Trim().Length > 0 && _serial.Text.Trim().Length > 0 && _code.Password.Trim().Length >= 4 && Store.Phase is not ConnectionPhase.Connecting;

    private void OnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        Connect();
        e.Handled = true;
    }

    private void Connect()
    {
        if (!CanConnect) return;
        var serial = _serial.Text.Trim().ToUpperInvariant();
        var config = new PrinterConfig
        {
            Host = _host.Text.Trim(), Serial = serial, Name = _name.Text.Trim().Length == 0 ? serial : _name.Text.Trim(), ModelCode = _modelCode,
        };
        var firstPrinter = !Adding;
        Store.Connect(config, _code.Password.Trim(), _alternativeCodes);
        Navigation.AddingPrinter = false;
        if (firstPrinter && _openAtLogin.IsChecked == true && !LoginItem.Shared.IsEnabled && !Store.IsDemo) LoginItem.Shared.SetEnabled(true);
        // Ready for the next printer.
        _code.Password = "";
        _codeSource = null;
        _selected = null;
        foreach (var box in new[] { _host, _serial, _name }) Set(box, "");
    }
}
