using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BambuBar.App;
using BambuBar.Models;
using BambuBar.Services;
using BambuBar.UI.Widgets;
using static BambuBar.UI.Pages.PageKit;
using static BambuBar.UI.Ui;

namespace BambuBar.UI.Pages;

/// <summary>The desktop widgets, shown off: one widget on a desktop backdrop that morphs between its three sizes while
/// a simulated print plays through every state, the features each size carries, and buttons to put one on the desktop.</summary>
public sealed class WidgetsPage : ContentControl
{
    private static PrinterStore Store => PrinterStore.Shared;

    private readonly WidgetShowcase _showcase = new();

    public WidgetsPage()
    {
        Focusable = false;
        Content = new Live(() => (Store.HasMultiplePrinters, string.Join("|", Store.Sessions.Select(s => s.Config.Name)), DesktopWidgets.Shared.Placements.Count),
            Build);
        DesktopWidgets.Shared.Changed += Live.Invalidate;
    }

    private UIElement Build() => Column(22,
        PageHeader("Widgets", "Your printers on the desktop, in three sizes."),
        Reuse.Place(_showcase),
        HowTo());

    private static FrameworkElement HowTo()
    {
        var buttons = new List<UIElement?>();
        foreach (var size in Enum.GetValues<WidgetSize>())
        {
            var chosen = size;
            buttons.Add(ControlButton($"Add {size.Title()}", Icon.Add, () => Shell.AddWidget(chosen), Palette.AccentDeep, prominent: size == WidgetSize.Small).Sized(width: 130));
        }
        var count = DesktopWidgets.Shared.Placements.Count;
        var steps = Column(12,
            Step(1, Icon.Add, "Click a size below. The widget appears at the top right of your main screen."),
            Step(2, Icon.Hand, "Drag it where you want it. It stays under your other windows, like a desktop widget, and comes back there next time."),
            Step(3, Icon.Printer, Store.HasMultiplePrinters
                ? "**Right-click** a widget to choose its printer, size, or to keep it above other windows. Add one widget per printer to watch them side by side; a widget left on **Follow the tray icon** shows the tray's printer."
                : "**Right-click** a widget to change its size or keep it above other windows. Add more printers later and each widget can show a different one."),
            Row(10, [.. buttons]).Aligned(HorizontalAlignment.Left).WithMargin(34, 4, 0, 0),
            count > 0 ? Text(count == 1 ? "One widget is on your desktop." : $"{count} widgets are on your desktop.", 11.5, color: Theme.Secondary).WithMargin(34, 0, 0, 0) : null);
        return Column(10, SectionLabel("Add a widget"), Panel(steps, 16, 16));
    }

    private static FrameworkElement Step(int n, Icon icon, string text)
    {
        var number = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Theme.Brush(Palette.Accent, 0.15),
            Child = Text($"{n}", 12, Bold, Theme.Brush(Palette.Accent), display: true).Aligned(HorizontalAlignment.Center),
        };
        number.VerticalAlignment = VerticalAlignment.Top;
        return Row(12, number, Icons.Make(icon, 13, Theme.Secondary).Sized(18).WithMargin(0, 3, 0, 0).Aligned(vertical: VerticalAlignment.Top),
            Grow(Rich(text, 12.5, Theme.Primary).WithMargin(0, 2, 0, 0)));
    }
}

public enum ShowcaseScene { Printing, Preparing, Paused, Finished, Idle }

public enum WidgetFeature { Progress, TimeLeft, FinishTime, Layers, Stage, Temperatures, Spools, Printer, Errors, LastPrint }

public static class ShowcaseInfo
{
    public const double Duration = 3.6;

    public static string Title(this ShowcaseScene scene) => scene switch
    {
        ShowcaseScene.Printing => "Printing",
        ShowcaseScene.Preparing => "Heating",
        ShowcaseScene.Paused => "Paused",
        ShowcaseScene.Finished => "Finished",
        _ => "Ready",
    };

    public static Icon Icon(this ShowcaseScene scene) => scene switch
    {
        ShowcaseScene.Printing => UI.Icon.Printer,
        ShowcaseScene.Preparing => UI.Icon.Flame,
        ShowcaseScene.Paused => UI.Icon.Pause,
        ShowcaseScene.Finished => UI.Icon.CheckCircle,
        _ => UI.Icon.Moon,
    };

    public static Color Color(this ShowcaseScene scene) => scene switch
    {
        ShowcaseScene.Printing => Palette.Printing,
        ShowcaseScene.Preparing => Palette.Preparing,
        ShowcaseScene.Paused => Palette.Paused,
        ShowcaseScene.Finished => Palette.Finished,
        _ => Palette.IdleGray,
    };

    /// <summary>How much the showcase enlarges it, so every size fills the stage about equally.</summary>
    public static double StageScale(this WidgetSize size) => size switch
    {
        WidgetSize.Small => 1.3,
        WidgetSize.Medium => 1.1,
        _ => 1.0,
    };

    public static IReadOnlySet<WidgetFeature> Features(this WidgetSize size) => size switch
    {
        WidgetSize.Small => new HashSet<WidgetFeature> { WidgetFeature.Progress, WidgetFeature.TimeLeft, WidgetFeature.FinishTime, WidgetFeature.Layers, WidgetFeature.Stage, WidgetFeature.LastPrint, WidgetFeature.Temperatures, WidgetFeature.Spools },
        WidgetSize.Medium => new HashSet<WidgetFeature> { WidgetFeature.Progress, WidgetFeature.TimeLeft, WidgetFeature.FinishTime, WidgetFeature.Layers, WidgetFeature.Stage, WidgetFeature.Temperatures, WidgetFeature.Spools, WidgetFeature.Printer },
        _ => Enum.GetValues<WidgetFeature>().ToHashSet(),
    };

    public static string Summary(this WidgetSize size) => size switch
    {
        WidgetSize.Small => "The essentials at a glance: a progress ring with the percentage, time left and when it'll be done. Paused, it says why; finished, how long ago; idle, the temperatures and loaded spools.",
        WidgetSize.Medium => "The printer as it is: the part growing in the filament's colour, the job and its progress, finish time and layers, and nozzle, bed and chamber temperatures.",
        _ => "Everything: progress and timing, temperature tiles, every AMS spool with what's left, the newest error in plain words, and how the last print went.",
    };

    public static string Title(this WidgetFeature feature) => feature switch
    {
        WidgetFeature.Progress => "Progress",
        WidgetFeature.TimeLeft => "Time left",
        WidgetFeature.FinishTime => "Finish time",
        WidgetFeature.Layers => "Layers",
        WidgetFeature.Stage => "Stage & pause reason",
        WidgetFeature.Temperatures => "Temperatures",
        WidgetFeature.Spools => "AMS spools",
        WidgetFeature.Printer => "Live drawing",
        WidgetFeature.Errors => "Errors",
        _ => "Last print",
    };

    public static Icon Icon(this WidgetFeature feature) => feature switch
    {
        WidgetFeature.Progress => UI.Icon.Percent,
        WidgetFeature.TimeLeft => UI.Icon.Clock,
        WidgetFeature.FinishTime => UI.Icon.Flag,
        WidgetFeature.Layers => UI.Icon.Layers,
        WidgetFeature.Stage => UI.Icon.Pause,
        WidgetFeature.Temperatures => UI.Icon.Thermometer,
        WidgetFeature.Spools => UI.Icon.Spool,
        WidgetFeature.Printer => UI.Icon.Printer,
        WidgetFeature.Errors => UI.Icon.Warning,
        _ => UI.Icon.CheckCircle,
    };

    /// <summary>The snapshot <paramref name="t"/> seconds into the scene, so numbers move while it plays. The printer's
    /// own name, model and spools are used when there is one, so the tour previews your setup rather than made-up
    /// filament.</summary>
    public static SharedSnapshot Snapshot(this ShowcaseScene scene, double t, DateTime now)
    {
        var current = PrinterStore.Shared.Current;
        var name = current?.Config.Name ?? "P2S Workshop";
        var model = current?.Config.ModelName ?? "P2S";
        var live = current?.Status;

        var s = SamplePrinter.Status();
        s.JobName = "Articulated Dragon";
        s.UpdatedAt = now;
        if (live is not null && (live.Ams.Count > 0 || live.ExternalSpool?.IsLoaded == true))
        {
            s.Ams = live.Ams;
            s.ExternalSpool = live.ExternalSpool?.IsLoaded == true ? live.ExternalSpool : null;
            // The spool in use if it's printing, else the first loaded one, so the part has a real colour.
            var loaded = live.AllSpools.Where(sp => sp.IsLoaded).ToList();
            s.ActiveSpoolId = live.ActiveSpool?.Id ?? loaded.FirstOrDefault(sp => BambuColor.Luminance(sp.Color) < 0.85)?.Id ?? loaded.FirstOrDefault()?.Id;
        }
        s.Fit(PrinterModel.Named(model));
        var filament = s.ActiveSpool;
        JobRecord? lastJob = null;
        switch (scene)
        {
            case ShowcaseScene.Printing:
                // Races from 18 % to 96 % so the ring, bar, layers and time left all move.
                var p = Math.Min(0.96, 0.18 + t / Duration * 0.78);
                s.Progress = (int)(p * 100);
                s.TotalLayers = 284;
                s.Layer = (int)(s.TotalLayers * p);
                s.RemainingMinutes = Math.Max(1, (int)(148 * (1 - p)));
                s.Nozzle = new Temperature(220 + (t % 2 < 1 ? 1 : 0), 220);
                break;
            case ShowcaseScene.Preparing:
                s.State = JobState.Preparing;
                s.StageId = t < Duration * 0.55 ? 7 : 1;
                s.Progress = 0;
                s.Layer = 0;
                s.RemainingMinutes = 152;
                s.Nozzle = new Temperature(Math.Min(220, 95 + t * 40), 220);
                s.Bed = new Temperature(Math.Min(55, 30 + t * 9), 55);
                break;
            case ShowcaseScene.Paused:
                s.State = JobState.Paused;
                s.StageId = 6;
                s.Progress = 64;
                s.Layer = 182;
                s.TotalLayers = 284;
                s.RemainingMinutes = 54;
                s.Issues = [new PrinterIssue("HMS_0700_2300_0001_0004", IssueSeverity.Serious, "AMS A slot 4 filament has run out. Insert a new spool to continue.", null)];
                break;
            case ShowcaseScene.Finished:
                s.State = JobState.Finished;
                s.StageId = 255;
                s.Progress = 100;
                s.Layer = 284;
                s.TotalLayers = 284;
                s.RemainingMinutes = 0;
                s.ActiveSpoolId = null;
                s.Nozzle = new Temperature(Math.Max(38, 120 - t * 14), 0);
                s.Bed = new Temperature(Math.Max(30, 52 - t * 3), 0);
                lastJob = new JobRecord
                {
                    Name = s.JobName, StartedAt = now.AddHours(-3), EndedAt = now.AddMinutes(-12), Outcome = JobOutcome.Finished, TotalLayers = 284,
                    FilamentColor = filament?.Color ?? "E2763AFF", FilamentMaterial = filament?.Material ?? "PLA",
                };
                break;
            default:
                s.State = JobState.Idle;
                s.StageId = 255;
                s.JobName = "";
                s.Progress = 0;
                s.Layer = 0;
                s.TotalLayers = 0;
                s.RemainingMinutes = 0;
                s.ActiveSpoolId = null;
                if (s.ChamberLightOn is not null) s.ChamberLightOn = false;
                s.Nozzle = new Temperature(26, 0);
                s.Bed = new Temperature(24, 0);
                if (s.Chamber is not null) s.Chamber = 23;
                lastJob = new JobRecord
                {
                    Name = "Articulated Dragon", StartedAt = now.AddHours(-5), EndedAt = now.AddHours(-2), Outcome = JobOutcome.Finished, TotalLayers = 284,
                    FilamentColor = filament?.Color ?? "E2763AFF", FilamentMaterial = filament?.Material ?? "PLA",
                };
                break;
        }
        return new SharedSnapshot
        {
            Serial = "showcase", PrinterName = name, Model = model, Link = SharedSnapshot.LinkState.Online, Status = s, LastJob = lastJob, SavedAt = now,
        };
    }
}

/// <summary>The stage and the size's features. Every scene plays in one size, then the next size, on a loop.</summary>
public sealed class WidgetShowcase : ContentControl
{
    private const double StageMargin = 26, ScenesHeight = 44, ScenesWidth = 470;

    private readonly DispatcherTimer _frame = new(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1 / 20.0) };
    private readonly ContentControl _widget = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private WidgetSize _size = WidgetSize.Small;
    private ShowcaseScene _scene = ShowcaseScene.Printing;
    private DateTime _sceneStart = DateTime.UtcNow;

    public WidgetShowcase()
    {
        Focusable = false;
        Content = new Live(() => (_size, _scene, Theme.Version), Build);
        _frame.Tick += (_, _) => Tick();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _sceneStart = DateTime.UtcNow; _frame.Start(); Tick(); }
            else _frame.Stop();
        };
    }

    /// <summary>Freezes the tour on a size and scene, for snapshots.</summary>
    public void Show(WidgetSize size, ShowcaseScene scene, double t = 2)
    {
        _size = size;
        _scene = scene;
        _sceneStart = DateTime.UtcNow.AddSeconds(-t);
        _frame.Stop();
        ((Live)Content).Update();
        Render(t);
    }

    private void Tick()
    {
        var t = (DateTime.UtcNow - _sceneStart).TotalSeconds;
        if (t >= ShowcaseInfo.Duration)
        {
            Advance();
            t = 0;
        }
        Render(Math.Min(ShowcaseInfo.Duration, t));
    }

    private void Render(double t)
    {
        var now = DateTime.UtcNow;
        var widget = WidgetViews.Framed(_size, _scene.Snapshot(t, now), now);
        var scale = _size.StageScale();
        widget.LayoutTransform = new ScaleTransform(scale, scale);
        _widget.Content = widget;
    }

    private void Advance()
    {
        var scenes = Enum.GetValues<ShowcaseScene>();
        var next = (Array.IndexOf(scenes, _scene) + 1) % scenes.Length;
        _scene = scenes[next];
        if (next == 0)
        {
            var sizes = Enum.GetValues<WidgetSize>();
            _size = sizes[(Array.IndexOf(sizes, _size) + 1) % sizes.Length];
        }
        _sceneStart = DateTime.UtcNow;
        ((Live)Content).Update();
    }

    private void Pick(WidgetSize size)
    {
        _size = size;
        _sceneStart = DateTime.UtcNow;
        ((Live)Content).Update();
        Render(0);
    }

    private void Pick(ShowcaseScene scene)
    {
        _scene = scene;
        _sceneStart = DateTime.UtcNow;
        ((Live)Content).Update();
        Render(0);
    }

    private UIElement Build()
    {
        var frame = _size.FrameSize();
        var scale = _size.StageScale();
        var stageWidth = Math.Max(frame.Width * scale + StageMargin * 2, ScenesWidth);
        var stageHeight = frame.Height * scale + StageMargin * 2 + ScenesHeight;
        var stage = new Grid { Width = stageWidth, Height = stageHeight, ClipToBounds = true };
        stage.Children.Add(Backdrop());
        var widgetArea = new Grid { Margin = new Thickness(0, 0, 0, ScenesHeight) };
        widgetArea.Children.Add(Reuse.Place(_widget));
        stage.Children.Add(widgetArea);
        var scenes = Scenes();
        scenes.VerticalAlignment = VerticalAlignment.Bottom;
        scenes.HorizontalAlignment = HorizontalAlignment.Center;
        scenes.Margin = new Thickness(0, 0, 0, 14);
        stage.Children.Add(scenes);
        var framed = new Border
        {
            Child = stage, CornerRadius = new CornerRadius(30), BorderBrush = Theme.Fill(0.08), BorderThickness = new Thickness(0.75),
            VerticalAlignment = VerticalAlignment.Top,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Direction = 270, Opacity = 0.2 },
        };
        // Clip the stage to the rounded frame.
        stage.Clip = new RectangleGeometry(new Rect(0, 0, stageWidth, stageHeight), 30, 30);
        return Row(20, framed, Grow(Column(14, SizePicker(), Features()).Aligned(vertical: VerticalAlignment.Top)));
    }

    /// <summary>A soft wallpaper behind the showcase widget.</summary>
    private static FrameworkElement Backdrop()
    {
        var grid = new Grid();
        grid.Children.Add(new Border { Background = new LinearGradientBrush(System.Windows.Media.Color.FromRgb(140, 102, 82), System.Windows.Media.Color.FromRgb(51, 38, 46), 45) });
        grid.Children.Add(new Border
        {
            Background = new RadialGradientBrush(System.Windows.Media.Color.FromArgb(140, 242, 158, 97), Colors.Transparent)
            { Center = new Point(0.15, 0.15), GradientOrigin = new Point(0.15, 0.15), RadiusX = 0.7, RadiusY = 0.7 },
        });
        grid.Children.Add(new Border
        {
            Background = new RadialGradientBrush(System.Windows.Media.Color.FromArgb(115, 77, 115, 204), Colors.Transparent)
            { Center = new Point(0.9, 0.9), GradientOrigin = new Point(0.9, 0.9), RadiusX = 0.75, RadiusY = 0.75 },
        });
        return grid;
    }

    private FrameworkElement SizePicker()
    {
        // A segmented control: three toggle buttons in a track.
        var row = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1 };
        foreach (var size in Enum.GetValues<WidgetSize>())
        {
            var chosen = size;
            var selected = size == _size;
            var label = Text(size.Title(), 12, selected ? Semibold : Medium).Aligned(HorizontalAlignment.Center);
            var segment = new Border { Child = label, CornerRadius = new CornerRadius(6), Padding = new Thickness(0, 4, 0, 5) };
            row.Children.Add(Button(segment, () => Pick(chosen), t =>
            {
                segment.Background = selected ? Theme.Brush(Theme.Dark ? System.Windows.Media.Color.FromRgb(0x5A, 0x5A, 0x5E) : Colors.White)
                    : t.IsMouseOver ? Theme.Fill(0.06) : Brushes.Transparent;
            }));
        }
        return new Border { Child = row, CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Background = Theme.Fill(0.08) };
    }

    /// <summary>The states the tour walks through, on a frosted bar under the widget; clicking one jumps there.</summary>
    private FrameworkElement Scenes()
    {
        using var dark = Theme.Override(true);
        var row = new Flex { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var s in Enum.GetValues<ShowcaseScene>())
        {
            var chosen = s;
            var on = s == _scene;
            var content = Row(5,
                Icons.Make(s.Icon(), 10.5, on ? Theme.Brush(s.Color()) : Theme.Brush(Colors.White, 0.75)),
                Text(s.Title(), 11.5, on ? Semibold : Medium, Theme.Brush(Colors.White, on ? 1 : 0.8)));
            var pill = new Border
            {
                Child = content, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(14),
                Background = on ? Theme.Brush(Colors.White, 0.18) : Brushes.Transparent,
                BorderBrush = on ? Theme.Brush(s.Color(), 0.55) : Brushes.Transparent, BorderThickness = new Thickness(0.75),
            };
            row.Children.Add(Button(pill, () => Pick(chosen), null, $"Show the widget while {s.Title().ToLowerInvariant()}"));
        }
        return new Border
        {
            Child = row, Padding = new Thickness(3), CornerRadius = new CornerRadius(18),
            Background = Theme.Brush(Colors.Black, 0.42), BorderBrush = Theme.Brush(Colors.White, 0.12), BorderThickness = new Thickness(0.75),
        };
    }

    private FrameworkElement Features()
    {
        var chips = new FlowPanel { Spacing = 6, LineSpacing = 6 };
        var features = _size.Features();
        foreach (var feature in Enum.GetValues<WidgetFeature>())
        {
            var on = features.Contains(feature);
            var color = on ? Theme.Brush(Palette.Accent) : Theme.Brush(Theme.PrimaryColor, 0.36);
            chips.Children.Add(new Border
            {
                Child = Row(4, Icons.Make(feature.Icon(), 10, color), Text(feature.Title(), 11, Medium, color)),
                Padding = new Thickness(9, 4, 9, 4), CornerRadius = new CornerRadius(12),
                Background = on ? Theme.Brush(Palette.Accent, 0.13) : Theme.Fill(0.04),
            });
        }
        return Panel(Column(10, Text(_size.Title(), 17, Semibold), Text(_size.Summary(), 12.5, color: Theme.Secondary, wrap: true), chips), 16, 14);
    }
}
