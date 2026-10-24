using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BambuBar.Models;
using BambuBar.Platform;
using BambuBar.Services;
using BambuBar.UI;
using BambuBar.UI.Pages;
using static BambuBar.UI.PanelViews;
using static BambuBar.UI.Ui;

namespace BambuBar.App;

/// <summary>The main window: a sidebar of pages beside the page itself, on Mica. Closing it hides it; the app keeps
/// running in the tray.</summary>
internal sealed class MainWindow : Window
{
    private static PrinterStore Store => PrinterStore.Shared;
    private static Navigation Navigation => Navigation.Shared;
    private const string BoundsKey = "mainWindowBounds";

    private readonly OverviewPage _overview = new();
    private readonly SetupPage _setup = new();
    private readonly FilamentPageHost _filament = new();
    private readonly WidgetsPage _widgets = new();
    private readonly SettingsPage _settings = new();
    private readonly AboutPage _about = new();
    private readonly FleetPageHost _fleet = new();
    private readonly ScrollViewer _scroller;
    private readonly Border _layer = new() { CornerRadius = new CornerRadius(8, 0, 0, 0), BorderThickness = new Thickness(1, 1, 0, 0) };
    private readonly Border _wash = new() { CornerRadius = new CornerRadius(8, 0, 0, 0) };
    private readonly Border _corner = new() { CornerRadius = new CornerRadius(8, 0, 0, 0) };
    private AppSection _shownSection;
    private bool _mica;
    private JobState? _washState;
    private int _washVersion;

    public bool Quitting { get; set; }

    public MainWindow()
    {
        Title = "Bambu Bar";
        Width = 980;
        Height = 700;
        MinWidth = 820;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Resources/BambuBar.ico"));
        LoadBounds();

        var page = new Live(() => (PageFor(Navigation.Section, Navigation.AddingPrinter), Theme.Version), () =>
        {
            var shown = PageFor(Navigation.Section, Navigation.AddingPrinter);
            if (_shownSection != Navigation.Section) _scroller?.ScrollToTop();
            _shownSection = Navigation.Section;
            return Reuse.Place(shown);
        });
        var centered = new Border { MaxWidth = 860, Padding = new Thickness(28, 24, 28, 24), Child = page };
        _scroller = new ScrollViewer
        {
            Content = centered,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            PanningMode = PanningMode.VerticalOnly,
        };
        var detail = new Grid();
        detail.Children.Add(_layer);
        detail.Children.Add(_wash);
        detail.Children.Add(_corner);
        detail.Children.Add(_scroller);
        detail.Children.Add(new Live(() => (UpdateChecker.Shared.Offered, Theme.Version), UpdateOffer));

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new Live(SidebarKey, Sidebar);
        Grid.SetColumn(sidebar, 0);
        Grid.SetColumn(detail, 1);
        root.Children.Add(sidebar);
        root.Children.Add(detail);
        Content = root;

        SourceInitialized += (_, _) => ApplyTheme();
        Theme.Changed += ApplyTheme;
        Store.Changed += UpdateWash;
        PreviewKeyDown += OnKey;
        Activated += (_, _) => LoginItem.Shared.Refresh();
    }

    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // Windows only lets the foreground app take focus; Topmost for a moment brings the window up from the tray.
        Topmost = true;
        Topmost = false;
        Focus();
        Live.Invalidate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveBounds();
        if (!Quitting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private UIElement PageFor(AppSection section, bool adding) => section switch
    {
        AppSection.Fleet => Store.HasMultiplePrinters && !adding ? _fleet : Store.IsConfigured && !adding ? _overview : _setup,
        AppSection.Overview => Store.IsConfigured && !adding ? _overview : _setup,
        AppSection.Filament => _filament,
        AppSection.Widgets => _widgets,
        AppSection.Settings => _settings,
        _ => _about,
    };

    // MARK: - Sidebar

    private static object SidebarKey() => (Navigation.Section, Store.HasMultiplePrinters, Store.Current?.Id, Store.Current?.Config.Name,
        Store.Current?.ShortState, Store.Current?.IsOnline, Store.Status?.State, Store.Status?.Progress, Store.LinkSummary, Store.Phase);

    private UIElement Sidebar()
    {
        var items = new List<UIElement?>();
        if (Store.HasMultiplePrinters) items.Add(SidebarPicker().WithMargin(0, 0, 0, 6));
        foreach (var section in AppSections.All)
        {
            if (section == AppSection.Fleet && !Store.HasMultiplePrinters) continue;
            items.Add(SidebarRow(section));
        }
        var list = Column(2, [.. items]);
        list.VerticalAlignment = VerticalAlignment.Top;
        var grid = new Grid { Margin = new Thickness(10, 12, 10, 0) };
        grid.Children.Add(list);
        if (!Store.HasMultiplePrinters)
        {
            var footer = SidebarFooter();
            footer.VerticalAlignment = VerticalAlignment.Bottom;
            grid.Children.Add(footer);
        }
        return grid;
    }

    private static FrameworkElement SidebarRow(AppSection section)
    {
        var selected = Navigation.Section == section;
        var accent = Theme.Brush(Palette.Accent);
        var indicator = new Border
        {
            Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), Background = accent,
            Visibility = selected ? Visibility.Visible : Visibility.Hidden, VerticalAlignment = VerticalAlignment.Center,
        };
        var content = Row(12, indicator, Icons.Make(section.Icon(), 15, selected ? accent : Theme.Primary).Sized(18), Text(section.Title(), 13, selected ? Semibold : FontWeights.Normal));
        var body = new Border { Child = content, CornerRadius = new CornerRadius(5), Padding = new Thickness(0, 8, 10, 8) };
        return Button(body, () => Navigation.Section = section, t =>
        {
            body.Background = selected ? Theme.Fill(t.IsMouseOver ? 0.09 : 0.06) : t.IsPressed ? Theme.Fill(0.05) : t.IsMouseOver ? Theme.Fill(0.04) : Brushes.Transparent;
        });
    }

    /// <summary>The selector at the top of the sidebar: the drawn printer, its name and state.</summary>
    private static FrameworkElement SidebarPicker()
    {
        if (Store.Current is not { } current) return new Border();
        var content = Row(10,
            new PrinterArtView(PrinterArt.For(current.Status, current.Config.ModelName, dimmed: !current.IsOnline, animated: false), 26, 31),
            Grow(Column(1, Text(current.Config.Name, 12.5, Semibold),
                Row(4, SessionDot(current), Text(current.ShortState, 10.5, color: Theme.Secondary)).Aligned(HorizontalAlignment.Left))),
            Icons.Make(UI.Icon.ChevronDown, 10, Theme.Secondary));
        var body = new Border { Child = content, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(10) };
        Tap? picker = null;
        picker = Button(body, () => ShowPrinterMenu(picker!), t => body.Background = Theme.Fill(t.IsMouseOver ? 0.09 : 0.06), "Choose a printer");
        return picker;
    }

    /// <summary>Printer name and live state, always visible under the sidebar.</summary>
    private static FrameworkElement SidebarFooter()
    {
        string line;
        if (!Store.IsConfigured) line = "Set up to begin";
        else if (Store.Status is not { } s || !Store.IsOnline) line = Store.LinkSummary;
        else line = s.State.IsActive() ? $"{s.State.Title()} · {s.Progress}%" : s.State.Title();
        return Row(10,
            new PrinterArtView(PrinterArt.For(Store.Status, Store.Config?.ModelName, dimmed: !Store.IsOnline, animated: false), 26, 31),
            Grow(Column(1, Text(Store.IsConfigured ? Store.PrinterName : "No printer", 12, Semibold), Text(line, 10.5, color: Theme.Secondary))))
            .WithMargin(4, 12, 4, 12);
    }

    // MARK: - Update offer

    /// <summary>A newer release, offered once in the window's corner (Later hides it until the next launch).</summary>
    private static UIElement? UpdateOffer()
    {
        var updates = UpdateChecker.Shared;
        if (updates.Offered is not { } release) return null;
        var buttons = Row(8, Spacer(),
            Push("Skip This Version", () => updates.Skip(release)),
            Push("Later", updates.DismissOffer),
            Push("Download", () => { Open(release.PageUrl); updates.DismissOffer(); }, accent: true));
        var card = new Border
        {
            Width = 380, Padding = new Thickness(16), CornerRadius = new CornerRadius(16),
            Background = Theme.Brush(Theme.FlyoutColor), BorderBrush = Theme.Fill(0.1), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 8, Direction = 270, Opacity = 0.3 },
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(22),
            Child = Column(12,
                Row(12, Icons.Make(UI.Icon.Download, 26, Theme.Brush(Palette.Accent)).Aligned(vertical: VerticalAlignment.Top),
                    Grow(Column(3, Text($"Bambu Bar {release.Version} is available", 13.5, Semibold),
                        Text($"You have {updates.CurrentVersion}. Download the new BambuBar.exe from GitHub and replace this one; your printers and settings stay.", 12, color: Theme.Secondary, wrap: true)))),
                buttons),
        };
        // Slides in from the right.
        var slide = new TranslateTransform(40, 0);
        card.RenderTransform = slide;
        card.Opacity = 0;
        card.Loaded += (_, _) =>
        {
            var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease });
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
        };
        return card;
    }

    // MARK: - Look

    private void ApplyTheme()
    {
        _mica = Backdrop.Apply(this, Backdrop.Kind.Mica, Theme.Dark);
        if (!_mica) Background = Theme.Brush(Theme.WindowColor);
        // The page sits on a slightly lighter layer than the sidebar, as in Windows 11's own apps.
        _layer.Background = Theme.Dark ? Theme.Brush(Color.FromRgb(0x3A, 0x3A, 0x3A), _mica ? 0.3 : 0.5) : Theme.Brush(Colors.White, _mica ? 0.5 : 0.7);
        _layer.BorderBrush = Theme.Fill(Theme.Dark ? 0.08 : 0.06);
        UpdateWash();
    }

    /// <summary>A faint wash in the colour of the current state behind every page.</summary>
    private void UpdateWash()
    {
        var state = Store.Status?.State;
        if (state == _washState && _wash.Background is not null && _washVersion == Theme.Version) return;
        _washState = state;
        _washVersion = Theme.Version;
        _wash.Background = PageKit.PageWash(Store.Status, top: true);
        _corner.Background = PageKit.PageWash(Store.Status, top: false);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && Store.HasMultiplePrinters)
        {
            var sysKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if (sysKey == Key.Left) { Store.SelectPrevious(); e.Handled = true; return; }
            if (sysKey == Key.Right) { Store.SelectNext(); e.Handled = true; return; }
        }
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        var sections = AppSections.All.Where(s => s != AppSection.Fleet).ToList();
        var digit = e.Key is >= Key.D0 and <= Key.D9 ? e.Key - Key.D0 : e.Key is >= Key.NumPad0 and <= Key.NumPad9 ? e.Key - Key.NumPad0 : -1;
        if (digit == 0 && Store.HasMultiplePrinters) Navigation.Section = AppSection.Fleet;
        else if (digit >= 1 && digit <= sections.Count) Navigation.Section = sections[digit - 1];
        else if (e.Key == Key.R) Store.Refresh();
        else if (e.Key == Key.L && Store.IsOnline && Store.Status?.ChamberLightOn is not null) Store.ToggleLight();
        else if (e.Key == Key.OemComma) Navigation.Section = AppSection.Settings;
        else if (e.Key == Key.W) Close();
        else return;
        e.Handled = true;
    }

    // MARK: - Size and place

    private sealed record Bounds(double Left, double Top, double Width, double Height, bool Maximized);

    private void LoadBounds()
    {
        if (SettingsStore.Shared.Get<Bounds>(BoundsKey) is not { } b) return;
        // Only where it's still on a screen.
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!screen.IntersectsWith(new Rect(b.Left + 40, b.Top + 10, Math.Max(1, b.Width - 80), 40))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = b.Left;
        Top = b.Top;
        Width = Math.Max(MinWidth, b.Width);
        Height = Math.Max(MinHeight, b.Height);
        if (b.Maximized) WindowState = WindowState.Maximized;
    }

    private void SaveBounds()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.IsEmpty) return;
        SettingsStore.Shared.SetObject(BoundsKey, new Bounds(r.Left, r.Top, r.Width, r.Height, WindowState == WindowState.Maximized));
    }
}

/// <summary>Pages that are a plain build from state, hosted so they stay put across visits.</summary>
internal sealed class FleetPageHost : ContentControl
{
    public FleetPageHost() { Focusable = false; Content = new Live(FleetPage.Key, FleetPage.Build); }
}

internal sealed class FilamentPageHost : ContentControl
{
    public FilamentPageHost() { Focusable = false; Content = new Live(FilamentPage.Key, FilamentPage.Build); }
}
