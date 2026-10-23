using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BambuBar.App;
using BambuBar.Models;
using BambuBar.Protocol;
using BambuBar.Services;
using static BambuBar.UI.Components;
using static BambuBar.UI.Ui;

namespace BambuBar.UI;

/// <summary>The pieces of the tray panel, several of them reused by the main window.</summary>
public static class PanelViews
{
    private static PrinterStore Store => PrinterStore.Shared;

    // MARK: - Job hero

    /// <summary>The printer beside the job: name, stage, big percentage, filament-coloured progress and timing.</summary>
    public static FrameworkElement JobHero(PrinterStatus status, bool compact, PrinterModelView model)
    {
        var art = PrinterArt.For(status, Store.Config?.ModelName, lightOverride: Store.ChamberLightOn);
        // After a print the part stays on the plate in the colour it was printed in.
        if (art.Filament is null && !status.State.IsActive() && Store.LastJob?.FilamentColor is { } last) art.Filament = Palette.Filament(last);
        model.Apply(art);
        Reuse.Detach(model);
        model.Width = compact ? 92 : 190;
        model.Height = compact ? 110 : 228;

        var pill = StatePill(status.State, PillText(status) ?? status.StateTitle, compact, Palette.ColorFor(status));
        var body = status.State == JobState.Idle ? IdleBody(compact) : JobBody(status, compact);
        var column = Column(compact ? 6 : 10, [pill, .. body]);
        var row = Row(compact ? 12 : 22, model, Grow(column));
        row.VerticalAlignment = VerticalAlignment.Center;
        model.VerticalAlignment = VerticalAlignment.Center;
        column.VerticalAlignment = VerticalAlignment.Center;
        var panel = Panel(row, compact ? 20 : 26, compact ? 12 : 22);
        if (status.ChamberLightOn is null) return panel;
        var toggle = LightToggle(compact);
        toggle.HorizontalAlignment = HorizontalAlignment.Right;
        toggle.VerticalAlignment = VerticalAlignment.Top;
        toggle.Margin = new Thickness(compact ? 10 : 16);
        return Overlay(panel, toggle);
    }

    private static string? PillText(PrinterStatus status)
    {
        if (status.State == JobState.Printing && status.Speed != SpeedLevel.Standard) return $"Printing · {status.Speed.Title()}";
        if (status.State == JobState.Idle && status.DoorOpen == true) return "Ready · Door open";
        return null;
    }

    /// <summary>The stage while it's doing something special, otherwise the filament it's printing with.</summary>
    private static string Subtitle(PrinterStatus status)
    {
        if (status.State == JobState.Printing && (status.StageId is null || status.StageId == 0))
        {
            if (status.ActiveSpool is { IsLoaded: true } spool) return $"{spool.DisplayName} · {spool.Label}";
            return status.Speed == SpeedLevel.Standard ? "Printing" : $"{status.Speed.Title()} speed";
        }
        if (status.State == JobState.Finished && Store.LastJob is { Outcome: JobOutcome.Finished, EndedAt: { } end } job)
            return $"Finished {Format.Relative(end)}" + (job.Duration is { } d ? $" · took {Format.Duration(d)}" : "");
        if (status.State == JobState.Failed)
            return status.TotalLayers > 0 ? $"Stopped at layer {status.Layer} of {status.TotalLayers}" : "Stopped";
        return status.DetailText;
    }

    private static Color Tint(PrinterStatus status)
    {
        if (status.State == JobState.Paused) return Palette.Paused;
        if (status.State == JobState.Failed) return status.Canceled ? Theme.PrimaryColor.WithOpacity(0.6) : Palette.Failed;
        if (status.State == JobState.Finished) return Palette.Finished;
        if (status.ActiveSpool is { } spool)
        {
            // Very light or very dark filament would vanish against the track; use the accent instead.
            var l = BambuColor.Luminance(spool.Color);
            if (l <= 0.88 && l >= 0.12 && Palette.Filament(spool.Color) is { } c) return c;
        }
        return Palette.Accent;
    }

    private static UIElement[] JobBody(PrinterStatus status, bool compact)
    {
        var name = Text(status.JobName.Length == 0 ? "Untitled print" : status.JobName, compact ? 14 : 20, Semibold);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        if (!compact) { name.TextWrapping = TextWrapping.Wrap; name.MaxHeight = 56; }
        var title = Column(2, name, Text(Subtitle(status), compact ? 11.5 : 13, color: Theme.Secondary));
        if (status.State == JobState.Preparing && status.Progress == 0)
        {
            // Before the first layer there's no percentage worth showing: the stage is the news.
            var preparing = Row(8, new Spinner(compact ? 14 : 18), Text("Getting ready", compact ? 18 : 28, Semibold, display: true));
            preparing.Margin = new Thickness(0, compact ? 4 : 8, 0, compact ? 4 : 8);
            return [title, preparing, Timing(status, compact)];
        }
        var percent = Row(2,
            Text($"{status.Progress}", compact ? 34 : 56, Semibold, display: true, digits: true),
            Text("%", compact ? 17 : 26, Semibold, Theme.Secondary, display: true).Aligned(vertical: VerticalAlignment.Bottom).WithMargin(0, 0, 0, compact ? 5 : 9),
            Spacer(4),
            status.TotalLayers > 0
                ? Column(0,
                    Text($"{status.Layer}/{status.TotalLayers}", compact ? 12 : 14, Semibold, display: true, digits: true).Aligned(HorizontalAlignment.Right),
                    Text("layers", 10, color: Theme.Tertiary).Aligned(HorizontalAlignment.Right)).Aligned(vertical: VerticalAlignment.Bottom).WithMargin(0, 0, 0, compact ? 4 : 8)
                : null);
        percent.Margin = new Thickness(0, compact ? -4 : 0, 0, 0);
        return [title, percent, FilamentProgressBar(status.Progress / 100.0, Tint(status), compact ? 7 : 10), Timing(status, compact)];
    }

    private static FrameworkElement Timing(PrinterStatus status, bool compact)
    {
        var size = compact ? 11.5 : 13;
        if (status.State == JobState.Finished) return TightLabel(Icon.Hand, "Ready to remove", size);
        if (status.State == JobState.Failed && status.Canceled) return TightLabel(Icon.XCircle, "Clear the plate to print again", size);
        if (status.State == JobState.Failed)
        {
            var red = Theme.Brush(Palette.Failed);
            return TightLabel(Icon.Warning, status.Issues.Count == 0 ? "Clear the plate to print again" : "Needs attention", size, red, red);
        }
        if (status.EstimatedEnd is { } end)
        {
            return Row(6,
                TightLabel(Icon.Clock, Format.Duration(status.RemainingMinutes) + " left", size),
                Spacer(4),
                status.State != JobState.Paused ? Text($"Done {Format.Finish(end)}", size, Medium, Theme.Secondary) : null);
        }
        return TightLabel(Icon.Clock, "Estimating…", size);
    }

    private static UIElement[] IdleBody(bool compact)
    {
        var size = compact ? 11.5 : 13;
        if (Store.LastJob is { } last && last.Outcome != JobOutcome.Running)
        {
            var ok = last.Outcome == JobOutcome.Finished;
            var lastLine = Text($"Last: {last.Name}", size, color: Theme.Secondary);
            return
            [
                Column(2, Text("Ready to print", compact ? 15 : 22, Semibold), lastLine),
                Row(6,
                    Icons.Make(ok ? Icon.CheckCircle : Icon.XCircle, size, Theme.Brush(ok ? Palette.Finished : Palette.Failed)),
                    Text(last.Outcome.Title(), size, Medium),
                    last.EndedAt is { } end ? Text($"· {Format.Relative(end)}", size, Medium, Theme.Secondary) : null,
                    last.Duration is { } d ? Text($"· {Format.Duration(d)}", size, Medium, Theme.Secondary) : null),
            ];
        }
        return
        [
            Column(3, Text("Ready to print", compact ? 15 : 22, Semibold),
                Text("Send a job from Bambu Studio or the printer and it shows up here.", size, color: Theme.Secondary, wrap: true)),
        ];
    }

    /// <summary>The chamber light, as a small switch in the corner of the printer card.</summary>
    public static FrameworkElement LightToggle(bool compact)
    {
        var on = Store.ChamberLightOn;
        var yellow = Theme.Brush(Palette.Yellow);
        var bulb = Icons.Make(Icon.Lightbulb, compact ? 11 : 12.5, on ? yellow : Theme.Secondary).Sized(compact ? 14 : 16);
        if (on) bulb.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Palette.Yellow, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.8 };
        var label = Row(5, bulb, Text("Light", compact ? 11 : 12, Semibold, on ? Theme.Primary : Theme.Secondary));
        var body = new Border
        {
            Child = label, CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(0.75),
            Padding = compact ? new Thickness(8, 4, 8, 4) : new Thickness(10, 6, 10, 6),
        };
        var tap = Button(body, () => Store.ToggleLight(), t =>
        {
            body.Background = on ? Theme.Brush(Palette.Yellow, 0.16) : Theme.Fill(t.IsMouseOver ? 0.10 : 0.06);
            body.BorderBrush = on ? Theme.Brush(Palette.Yellow, 0.4) : Theme.Fill(0.08);
        }, on ? "Turn the chamber light off" : "Turn the chamber light on");
        System.Windows.Automation.AutomationProperties.SetName(tap, $"Chamber light, {(on ? "on" : "off")}");
        return tap;
    }

    // MARK: - Filament

    /// <summary>One row per AMS unit (and the external spool), with spools, material and how much is left.</summary>
    public static FrameworkElement FilamentStrip(PrinterStatus status)
    {
        var rows = new List<UIElement>();
        foreach (var unit in status.Ams)
        {
            var spools = new Flex { Orientation = Orientation.Horizontal };
            foreach (var spool in unit.Spools) spools.Children.Add(Grow(SpoolColumn(status, spool)));
            if (ReferenceEquals(unit, status.Ams[^1]) && status.ExternalSpool is { IsLoaded: true } ext)
            {
                spools.Children.Add(Divider(vertical: true, length: 40).WithMargin(4, 0, 4, 0));
                spools.Children.Add(Grow(SpoolColumn(status, ext)));
            }
            rows.Add(Column(8, SectionLabel(unit.Title, Climate(unit)), spools));
        }
        if (status.Ams.Count == 0 && status.ExternalSpool is { IsLoaded: true } only)
        {
            rows.Add(SectionLabel("External spool"));
            rows.Add(Row(0, SpoolColumn(status, only), Spacer()));
        }
        return Panel(Column(9, [.. rows]), 18, 12);
    }

    private static string? Climate(AmsUnit unit)
    {
        var parts = new List<string>();
        if (unit.DryingMinutesLeft > 0) parts.Add($"Drying · {Format.Duration(unit.DryingMinutesLeft)}");
        if (unit.Humidity is int h) parts.Add($"{h}% RH");
        else if (unit.HumidityLevel is int level) parts.Add($"Humidity {level}/5");
        if (unit.Temperature is double t) parts.Add(Format.Temperature(t));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static FrameworkElement SpoolColumn(PrinterStatus status, Spool spool)
    {
        var active = spool.Id == status.ActiveSpoolId && status.State.IsActive();
        var swatch = new Grid { Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Center };
        swatch.Children.Add(new SpoolSwatch(spool, active, 34));
        if (active)
        {
            swatch.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Fill = Theme.Brush(Palette.Accent), Stroke = Theme.Brush(Theme.WindowColor), StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -2, 0),
            });
        }
        var material = Text(spool.IsLoaded ? spool.Material : spool.Label, 10.5, Semibold, spool.IsLoaded ? Theme.Primary : Theme.Tertiary);
        var amount = Text(spool.IsLoaded ? spool.AmountText : "Empty", 10, color: Theme.Secondary, digits: true);
        material.HorizontalAlignment = HorizontalAlignment.Center;
        amount.HorizontalAlignment = HorizontalAlignment.Center;
        var column = Column(4, swatch, material, amount);
        var detail = string.Join(", ", new[]
        {
            string.Join(" ", new[] { spool.Brand, spool.DisplayName }.Where(s => !string.IsNullOrEmpty(s))),
            spool.ColorName,
            spool.Remain is null ? null : $"{spool.RemainDetail} left",
        }.Where(s => !string.IsNullOrEmpty(s)));
        column.Background = Brushes.Transparent;
        return column.Tip(!spool.IsLoaded ? $"{spool.Label}: empty" : $"{spool.Label}: {detail}");
    }

    // MARK: - Controls

    private const string LockedHelp = "The printer accepts this only in LAN Only mode with Developer Mode on (Settings → Network on its screen).";

    public static FrameworkElement PrintControls(PrinterStatus status)
    {
        var locked = status.ControlsLocked;
        var row = new Flex { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (status.State == JobState.Paused)
            row.Children.Add(Grow(ControlButton("Resume", locked ? Icon.Lock : Icon.Play, () => Store.Resume(), Palette.AccentDeep, prominent: true, tip: locked ? LockedHelp : "Resume the print")));
        else if (status.State.IsActive())
            row.Children.Add(Grow(ControlButton("Pause", locked ? Icon.Lock : Icon.Pause, () => Store.Pause(), tip: locked ? LockedHelp : "Pause the print")));
        if (status.State.IsActive())
        {
            row.Children.Add(Grow(ControlButton("Stop", locked ? Icon.Lock : Icon.Stop, () => Store.StopPrint(), Palette.Failed, tip: locked ? LockedHelp : "Stop the print")));
            Tap? speed = null;
            speed = ControlButton("", SpeedIcon(Store.Speed), () => SpeedMenu(speed!), tip: $"Print speed: {Store.Speed.Title()}");
            speed.Width = 44;
            row.Children.Add(speed);
        }
        return row;
    }

    public static Icon SpeedIcon(SpeedLevel level) => level switch
    {
        SpeedLevel.Silent => Icon.SpeedSilent,
        SpeedLevel.Standard => Icon.SpeedStandard,
        SpeedLevel.Sport => Icon.Speed,
        _ => Icon.Bolt,
    };

    private static void SpeedMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var level in new[] { SpeedLevel.Silent, SpeedLevel.Standard, SpeedLevel.Sport, SpeedLevel.Ludicrous })
        {
            var item = new MenuItem
            {
                Header = $"{level.Title()} ({level.Percent()}%)",
                IsCheckable = true,
                IsChecked = Store.Speed == level,
                Icon = Icons.Make(SpeedIcon(level), 14, Theme.Primary),
            };
            item.Click += (_, _) => Store.SetSpeed(level);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // MARK: - Without a live printer

    public static FrameworkElement ConnectionCard()
    {
        var phase = Store.Phase;
        var title = phase switch
        {
            ConnectionPhase.Connecting => $"Connecting to {Store.PrinterName}…",
            ConnectionPhase.Unauthorized => "Access code rejected",
            ConnectionPhase.CertificateRejected => "Can't verify the printer",
            _ => $"{Store.PrinterName} is offline",
        };
        FrameworkElement actions;
        if (phase is ConnectionPhase.Connecting) actions = new Spinner();
        else if (phase is ConnectionPhase.Unauthorized && Store.ReplacementCode is { } known)
            actions = Row(8, Push($"Use Code from {known.Source}", () => Store.UseReplacementCode(), small: true),
                Push("Settings…", () => Shell.ShowMainWindow(AppSection.Settings), small: true));
        else
            actions = Row(8, Push("Try Again", () => Store.Refresh(), small: true), Push("Settings…", () => Shell.ShowMainWindow(AppSection.Settings), small: true));
        actions.HorizontalAlignment = HorizontalAlignment.Left;
        var text = Column(5,
            Text(title, 15, Semibold),
            Text(Store.LinkDetail ?? "Bambu Bar keeps trying in the background.", 11.5, color: Theme.Secondary, wrap: true),
            actions.WithMargin(0, 2, 0, 0));
        return Panel(Row(14, new PrinterArtView(PrinterArt.For(null, Store.Config?.ModelName, dimmed: true, animated: false), 70, 84), Grow(text)), 20, 14);
    }

    public static FrameworkElement SetupPrompt()
    {
        var discovered = Store.Discovered;
        var rows = new List<UIElement>
        {
            Row(14, new PrinterArtView(PrinterArt.For(null, animated: false), 70, 84),
                Grow(Column(4, Text("Connect your printer", 15, Semibold),
                    Text(discovered.Count == 0
                        ? "Looking for Bambu Lab printers on your network…"
                        : $"Found {(discovered.Count == 1 ? "a printer" : $"{discovered.Count} printers")} on your network.", 11.5, color: Theme.Secondary, wrap: true)))),
        };
        foreach (var printer in discovered.Take(3))
        {
            var content = Row(10,
                Icons.Make(Icon.Printer, 14, Theme.Brush(Palette.Accent)),
                Grow(Column(1, Text(printer.Name, 12, Semibold), Text($"{printer.ModelName} · {printer.Host}", 10.5, color: Theme.Secondary))),
                Icons.Make(Icon.ChevronRight, 10, Theme.Tertiary));
            rows.Add(Plain(Panel(content, 12, 9), () => Shell.ShowMainWindow(AppSection.Overview, printer), radius: 12, padding: new Thickness(0)));
        }
        rows.Add(ControlButton("Set Up…", null, () => Shell.ShowMainWindow(AppSection.Overview), Palette.AccentDeep, prominent: true));
        return Panel(Column(12, [.. rows]), 20, 14);
    }

    public static FrameworkElement IssueBanner(PrinterIssue issue)
    {
        var color = issue.Severity <= IssueSeverity.Fatal ? Palette.Failed : issue.Severity == IssueSeverity.Serious ? Palette.Paused : Palette.Preparing;
        var meta = Row(8, Text(issue.Id, 10, color: Theme.Secondary, mono: true),
            issue.WikiUrl is { } url ? Link("Wiki", () => Open(url), size: 10.5) : null);
        meta.HorizontalAlignment = HorizontalAlignment.Left;
        var content = Row(10,
            Icons.Make(issue.Severity <= IssueSeverity.Serious ? Icon.Warning : Icon.Info, 14, Theme.Brush(color)).Aligned(vertical: VerticalAlignment.Top),
            Grow(Column(3, Text(issue.Message, 11.5, Medium, wrap: true), meta)));
        return Panel(content, 14, 11, color);
    }

    /// <summary>A newer release, one line above the footer.</summary>
    public static FrameworkElement UpdateRow(UpdateChecker.Release release)
    {
        var updates = UpdateChecker.Shared;
        return Panel(Row(8,
            Icons.Make(Icon.Download, 13, Theme.Brush(Palette.Accent)),
            Grow(Text($"Bambu Bar {release.Version} is available", 11.5, Medium)),
            Plain(Text("Later", 11.5, color: Theme.Secondary), updates.DismissOffer, padding: new Thickness(6, 2, 6, 2)),
            Push("Download", () => { Open(release.PageUrl); updates.DismissOffer(); }, small: true)), 12, 10);
    }

    public static FrameworkElement ToastView(PrinterStore.ToastMessage toast) => Panel(Row(8,
        Icons.Make(toast.IsError ? Icon.Warning : Icon.CheckCircle, 13, Theme.Brush(toast.IsError ? Palette.Paused : Palette.Accent)).Aligned(vertical: VerticalAlignment.Top),
        Grow(Text(toast.Message, 11.5, Medium, wrap: true))), 12, 10);

    // MARK: - Printers

    /// <summary>A dot in the colour of a printer's state.</summary>
    public static FrameworkElement SessionDot(PrinterSession session)
    {
        Color color;
        if (session.Status is not { } status || !session.IsOnline) color = session.Phase is ConnectionPhase.Connecting ? Palette.Paused : Palette.Failed.WithOpacity(0.8);
        else color = Palette.ColorFor(status);
        return Dot(color, 7);
    }

    /// <summary>The printer's state as a small coloured label.</summary>
    public static FrameworkElement StateChip(PrinterSession session)
    {
        string title;
        Brush color;
        Color background;
        if (session.Status is not { } s || !session.IsOnline)
        {
            var connecting = session.Phase is ConnectionPhase.Connecting;
            title = connecting ? "Connecting" : "Offline";
            color = connecting ? Theme.Brush(Palette.Paused) : Theme.Secondary;
            background = connecting ? Palette.Paused : Theme.PrimaryColor.WithOpacity(0.6);
        }
        else
        {
            title = s.StateTitle;
            var c = Palette.ColorFor(s);
            color = Theme.Brush(c);
            background = c;
        }
        return new Border
        {
            Child = Text(title, 10.5, Semibold, color),
            Padding = new Thickness(7, 2, 7, 2),
            CornerRadius = new CornerRadius(10),
            Background = Theme.Brush(background, 0.14),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>Progress and time left for a printing job; finish time or nothing otherwise.</summary>
    public static FrameworkElement FleetProgress(PrinterSession session, bool compact = false)
    {
        if (session.Status is not { } s || !session.IsOnline || !(s.State.IsActive() || s.State == JobState.Finished))
            return new Border { Height = compact ? 18 : 21 };  // same height as a job, so cards line up
        string timing;
        switch (s.State)
        {
            case JobState.Finished: timing = "Done"; break;
            case JobState.Paused: timing = $"{Format.Duration(s.RemainingMinutes)} left · paused"; break;
            default:
                if (s.EstimatedEnd is not { } end) { timing = s.StageText ?? ""; break; }
                var left = Format.Duration(s.RemainingMinutes);
                // The finish time only when it's today; "tomorrow 06:01" doesn't fit beside the percentage.
                timing = end.ToLocalTime().Date == DateTime.Now.Date ? $"{left} · {Format.Finish(end)}" : $"{left} left";
                break;
        }
        var size = compact ? 10 : 10.5;
        return Column(4,
            FilamentProgressBar(s.Progress / 100.0, Palette.ColorFor(s), compact ? 4 : 5),
            Row(0, Text($"{s.Progress}%", size, Semibold, digits: true), Spacer(), Text(timing, size, color: Theme.Secondary, digits: true)));
    }

    /// <summary>The tray panel's list of every printer: one row each, click to see it.</summary>
    public static FrameworkElement FleetList(Action<PrinterSession> open)
    {
        var column = new Flex { Orientation = Orientation.Vertical, Spacing = 6 };
        foreach (var session in Store.FleetSessions)
        {
            var s = session.Status;
            var detail = s is not null && session.IsOnline && (s.State.IsActive() || s.State == JobState.Finished)
                ? FleetProgress(session, compact: true)
                : Text(session.FleetDetail, 10.5, color: Theme.Secondary);
            var content = Row(10,
                new PrinterArtView(PrinterArt.For(session.Status, session.Config.ModelName, dimmed: !session.IsOnline, animated: false), 30, 36),
                Grow(Column(3, Row(6, Grow(Text(session.Config.Name, 12.5, Semibold)), StateChip(session)), detail)));
            var background = session.NeedsAttention ? Theme.Brush(Palette.ColorFor(session.Status ?? new PrinterStatus()), 0.12) : Theme.Fill(0.05);
            var card = new Border { Child = content, Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(12), Background = background };
            var captured = session;
            column.Children.Add(Button(card, () => open(captured), t =>
            {
                card.Background = t.IsMouseOver && !captured.NeedsAttention ? Theme.Fill(0.08) : background;
            }, $"{session.Config.Name}: {session.FleetDetail}"));
        }
        return new ScrollViewer
        {
            Content = column, MaxHeight = 440, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    /// <summary>Switches which printer the panel and window show: each entry has the printer's state; "Add Printer…" at the end.</summary>
    public static void ShowPrinterMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var session in Store.Sessions)
        {
            var item = new MenuItem { Header = $"{session.Config.Name} — {session.ShortState}", IsCheckable = true, IsChecked = session.Id == Store.Current?.Id };
            var id = session.Id;
            item.Click += (_, _) => Store.Select(id);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "Add Printer…" };
        add.Click += (_, _) => Shell.AddPrinter();
        menu.Items.Add(add);
        menu.IsOpen = true;
    }
}

/// <summary>Asks the printer for a full report. The arrow spins until fresh data arrives, and the label says how
/// recent the data is, so it's clear what the button does and that it worked.</summary>
public sealed class RefreshControl : ContentControl
{
    private readonly bool _compact;
    private readonly RotateTransform _rotation = new();
    private readonly TextBlock _label = Ui.Text("", 11.5, color: Theme.Secondary, digits: true);
    private readonly Tap _button;
    private readonly System.Windows.Threading.DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime? _requestedAt;
    private bool _spinning;

    private static PrinterStore Store => PrinterStore.Shared;

    public RefreshControl(bool compact)
    {
        _compact = compact;
        var arrow = Icons.Make(Icon.Refresh, 12, Theme.Secondary);
        arrow.RenderTransformOrigin = new Point(0.5, 0.5);
        arrow.RenderTransform = _rotation;
        _button = Ui.HeaderIcon(arrow, Refresh, "Ask the printer for a full status report");
        Content = compact ? _button : Ui.Row(8, _label, _button);
        Focusable = false;
        _clock.Tick += (_, _) => Tick();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _clock.Start(); Tick(); }
            else _clock.Stop();
        };
        Store.Changed += OnStoreChanged;
    }

    private void OnStoreChanged()
    {
        // A reply after the request ends the spin.
        if (_spinning && Store.Current?.LastHeard is { } heard && _requestedAt is { } requested && heard >= requested) Finish();
        _button.IsEnabled = Store.IsConfigured;
    }

    private void Tick()
    {
        _button.IsEnabled = Store.IsConfigured;
        if (_compact) return;
        if (_spinning) { _label.Text = "Refreshing…"; return; }
        if (Store.Current?.LastHeard is not { } heard || !Store.IsOnline) { _label.Text = Store.LinkSummary; return; }
        var seconds = (int)(DateTime.UtcNow - heard).TotalSeconds;
        _label.Text = seconds < 3 ? "Updated just now" : seconds < 60 ? $"Updated {seconds}s ago" : "Updated " + Format.Relative(heard);
    }

    private void Refresh()
    {
        if (_spinning) return;
        _requestedAt = DateTime.UtcNow;
        _spinning = true;
        _rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.8)) { RepeatBehavior = RepeatBehavior.Forever });
        Tick();
        Store.Refresh();
        MainTimer.After(TimeSpan.FromSeconds(4), () => { if (_spinning) Finish(); });
    }

    private void Finish()
    {
        _spinning = false;
        // Finish the turn rather than snapping back.
        var current = _rotation.Angle % 360;
        _rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(current, 360, TimeSpan.FromSeconds(0.8 * (360 - current) / 360)));
        Tick();
    }
}
