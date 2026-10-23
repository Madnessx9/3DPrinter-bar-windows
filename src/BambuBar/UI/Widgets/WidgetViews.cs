using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BambuBar.Models;
using static BambuBar.UI.Components;
using static BambuBar.UI.Ui;

namespace BambuBar.UI.Widgets;

/// <summary>What a widget works out from a snapshot. <c>Date</c> is the moment drawn: progress and time left are
/// projected to it, so a widget keeps moving between the printer's reports.</summary>
public readonly record struct WidgetState(SharedSnapshot Snapshot, DateTime Date)
{
    public PrinterStatus? Status => Snapshot.Link is SharedSnapshot.LinkState.Online or SharedSnapshot.LinkState.Connecting ? Snapshot.Status : null;
    public bool IsLive => Status is not null;
    public JobState State => Status?.State ?? JobState.Idle;
    public string StateTitle => Status?.StateTitle ?? State.Title();
    public Color StateColor => Status is { } s ? Palette.ColorFor(s) : Palette.ColorFor(State);
    public double Progress => Status?.ProjectedProgress(Date) ?? 0;
    public string PercentText => ((int)Math.Floor(Progress * 100)).ToString();

    public int? MinutesLeft
    {
        get
        {
            if (Status is not { } s || s.EstimatedEnd is not { } end || !s.State.IsActive()) return null;
            if (s.State == JobState.Paused) return s.RemainingMinutes;
            return Math.Max(0, (int)Math.Ceiling((end - Date).TotalMinutes));
        }
    }

    public Color Tint
    {
        get
        {
            if (State == JobState.Paused) return Palette.Paused;
            if (State == JobState.Failed) return Status?.Canceled == true ? Theme.PrimaryColor.WithOpacity(0.6) : Palette.Failed;
            if (Status?.ActiveSpool is { } spool)
            {
                var l = BambuColor.Luminance(spool.Color);
                if (l is < 0.88 and > 0.12 && Palette.Filament(spool.Color) is { } c) return c;
            }
            return Palette.Accent;
        }
    }

    public string Headline
    {
        get
        {
            switch (Snapshot.Link)
            {
                case SharedSnapshot.LinkState.Unconfigured: return "Set up Bambu Bar";
                case SharedSnapshot.LinkState.Offline: return "Offline";
                case SharedSnapshot.LinkState.Unauthorized: return "Access code rejected";
                case SharedSnapshot.LinkState.Connecting when Status is null: return "Connecting…";
            }
            if (Status is not { } s) return "Offline";
            if (s.State == JobState.Idle) return "Ready";
            return s.JobName.Length == 0 ? s.State.Title() : s.JobName;
        }
    }

    public string StateLine
    {
        get
        {
            if (Status is not { } s)
            {
                return Snapshot.Link switch
                {
                    SharedSnapshot.LinkState.Unconfigured => "Open the app to connect your printer",
                    SharedSnapshot.LinkState.Unauthorized => "Enter the access code again in the app",
                    _ => LastJobLine ?? "Waiting for the printer",
                };
            }
            if (s.State == JobState.Printing && MinutesLeft is int left) return $"{Format.Duration(left)} left";
            if (s.State == JobState.Idle) return LastJobLine ?? "Ready to print";
            return s.DetailText;
        }
    }

    public string? LastJobLine => Snapshot.LastJob is { } last ? $"Last: {last.Name}" : null;

    public string? FinishText => MinutesLeft is int left && State == JobState.Printing ? Format.Finish(Date.AddMinutes(left), Date) : null;
}

/// <summary>The widget layouts of the macOS app, for the desktop widgets and the Widgets page. They always draw dark,
/// on the widget's own backdrop.</summary>
public static class WidgetViews
{
    public static Size FrameSize(this WidgetSize size) => size switch
    {
        WidgetSize.Small => new Size(170, 170),
        WidgetSize.Medium => new Size(364, 170),
        _ => new Size(364, 382),
    };

    public static string Title(this WidgetSize size) => size.ToString();

    public static UIElement View(WidgetSize size, SharedSnapshot snapshot, DateTime date) => size switch
    {
        WidgetSize.Small => SmallView(snapshot, date),
        WidgetSize.Medium => MediumView(snapshot, date),
        _ => LargeView(snapshot, date),
    };

    /// <summary>The widget with its chrome: rounded dark backdrop, hairline and padding, at its natural size.</summary>
    public static Border Framed(WidgetSize size, SharedSnapshot snapshot, DateTime date, bool shadow = true)
    {
        using var dark = Theme.Override(true);
        var frame = size.FrameSize();
        var backdrop = new LinearGradientBrush(Color.FromRgb(41, 43, 46), Color.FromRgb(20, 23, 26), 90);
        backdrop.Freeze();
        var card = new Border
        {
            CornerRadius = new CornerRadius(22),
            Background = backdrop,
            BorderBrush = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255)),
            BorderThickness = new Thickness(0.75),
            Padding = new Thickness(16),
            Child = View(size, snapshot, date),
            ClipToBounds = true,
            SnapsToDevicePixels = true,
        };
        TextOptions.SetTextFormattingMode(card, TextFormattingMode.Ideal);
        // The shadow goes on a frame around the card: on the card itself, its rectangular clip would cut the shadow
        // off at the card's bounds, leaving it only in the corners outside the rounding.
        var frameBorder = new Border { Width = frame.Width, Height = frame.Height, Child = card };
        if (shadow) frameBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Direction = 270, Opacity = 0.25 };
        return frameBorder;
    }

    private static TextBlock Rounded(string text, double size, FontWeight? weight = null, Brush? color = null) =>
        Text(text, size, weight ?? Semibold, color, display: true, digits: true);

    // MARK: - Small

    /// <summary>Numbers first: a progress ring with the percentage, time left and finish time, then the job. Every
    /// state leads with what's useful in it (the reason for a pause, how long ago a print finished, temperatures and
    /// spools when idle).</summary>
    public static UIElement SmallView(SharedSnapshot snapshot, DateTime date)
    {
        using var dark = Theme.Override(true);
        var w = new WidgetState(snapshot, date);
        var stateLabel = Text((w.IsLive ? w.StateTitle : w.Headline).ToUpperInvariant(), 9.5, Bold, w.IsLive ? Theme.Brush(w.StateColor) : Theme.Secondary);
        var top = Row(10, SmallDial(w).Sized(58, 58).Aligned(vertical: VerticalAlignment.Top),
            Grow(Column(1, [stateLabel, .. SmallPrimary(w, snapshot)]).Aligned(vertical: VerticalAlignment.Top)));
        top.VerticalAlignment = VerticalAlignment.Top;

        var bottom = new List<UIElement?>();
        if (w.Status is { } s && s.State.IsActive() && s.ActiveSpool is { IsLoaded: true } spool)
            bottom.Add(Row(5, new SpoolSwatch(spool, true, 15), Text($"{spool.DisplayName} · {spool.Label}", 10.5, Medium, Theme.Secondary)).WithMargin(0, 0, 0, 4));
        bottom.AddRange(SmallFooter(w, snapshot));
        var footer = Column(0, [.. bottom]);
        footer.VerticalAlignment = VerticalAlignment.Bottom;
        var grid = new Grid();
        grid.Children.Add(top);
        grid.Children.Add(footer);
        return grid;
    }

    private static IEnumerable<UIElement> SmallPrimary(WidgetState w, SharedSnapshot snapshot)
    {
        if (w.Status is not { } s)
        {
            yield return Text(snapshot.Link == SharedSnapshot.LinkState.Unconfigured ? "Open the app" : "No connection", 12, Semibold, wrap: true);
            yield break;
        }
        switch (s.State)
        {
            case JobState.Printing:
                yield return Fit(Rounded(w.MinutesLeft is int m ? Format.Duration(m) : "–", 19));
                yield return Text("left", 10.5, color: Theme.Secondary);
                break;
            case JobState.Preparing:
                yield return Lines(Text(s.StageText ?? "Getting ready", 12, Semibold, wrap: true), 3);
                break;
            case JobState.Paused:
                var reason = "Waiting";
                if (s.StageText is { } stage)
                {
                    var shorter = stage.Replace("Paused: ", "").Replace("Paused by ", "By ");
                    reason = shorter.Length > 0 ? char.ToUpper(shorter[0]) + shorter[1..] : shorter;
                }
                yield return Lines(Text(reason, 12, Semibold, wrap: true), 3);
                break;
            case JobState.Finished or JobState.Failed:
                if (snapshot.LastJob?.EndedAt is { } end)
                {
                    yield return Fit(Rounded(Ago(end, w.Date), 17));
                    if (snapshot.LastJob.Duration is { } d) yield return Text($"took {Format.Duration(d)}", 10.5, color: Theme.Secondary);
                }
                else
                {
                    yield return Rounded(s.State == JobState.Finished ? "Done" : "Stopped", 17);
                }
                break;
            default:
                var temps = new List<UIElement?>
                {
                    TempLine(Icon.Flame, s.Nozzle.Current, Palette.Nozzle, s.Nozzle.IsHeating),
                    TempLine(Icon.Heat, s.Bed.Current, Palette.Bed, s.Bed.IsHeating),
                };
                if (s.Chamber is double c) temps.Add(TempLine(Icon.Thermometer, c, Palette.Chamber, false));
                yield return Column(2, [.. temps]).WithMargin(0, 2, 0, 0);
                break;
        }
    }

    private static FrameworkElement TempLine(Icon icon, double value, Color tint, bool hot) =>
        Row(4, Icons.Make(icon, 9, hot ? Theme.Brush(tint) : Theme.Secondary).Sized(12), Rounded(Format.Temperature(value), 13));

    private static IEnumerable<UIElement> SmallFooter(WidgetState w, SharedSnapshot snapshot)
    {
        if (w.Status is { } s)
        {
            if (s.State == JobState.Idle)
            {
                // Idle: what's loaded, ready for the next job.
                var loaded = s.AllSpools.Where(sp => sp.IsLoaded).Take(5).ToList();
                if (loaded.Count > 0) yield return Row(5, [.. loaded.Select(sp => new SpoolSwatch(sp, false, 20))]).WithMargin(0, 0, 0, 4);
                yield return Text(snapshot.LastJob is { } last ? $"Last: {last.Name}" : snapshot.PrinterName, 11, color: Theme.Secondary);
            }
            else
            {
                yield return Text(s.JobName.Length == 0 ? snapshot.PrinterName : s.JobName, 12.5, Semibold);
                yield return Text(Detail(w, s), 10.5, color: Theme.Secondary, digits: true);
            }
        }
        else
        {
            yield return Text(snapshot.PrinterName, 12.5, Semibold);
            yield return Lines(Text(w.StateLine, 10.5, color: Theme.Secondary, wrap: true), 2);
        }
    }

    private static string Detail(WidgetState w, PrinterStatus s)
    {
        var layers = s.TotalLayers > 0 ? $"{s.Layer}/{s.TotalLayers}" : null;
        return s.State switch
        {
            JobState.Printing => string.Join(" · ", new[] { w.FinishText is { } f ? $"Done {f}" : null, layers }.OfType<string>()),
            JobState.Preparing => s.Nozzle.IsHeating ? $"Nozzle {(int)s.Nozzle.Current}° → {(int)s.Nozzle.Target}°" : "Starting soon",
            JobState.Paused => string.Join(" · ", new[] { layers is null ? null : $"Layer {layers}", $"{s.Progress}%" }.OfType<string>()),
            JobState.Finished => "Ready to remove" + (layers is null ? "" : $" · {layers}"),
            JobState.Failed => s.Canceled ? "Canceled" : s.Issues.FirstOrDefault()?.Message ?? "Print failed",
            _ => "",
        };
    }

    public static string Ago(DateTime utc, DateTime now)
    {
        var minutes = (int)((now - utc).TotalMinutes);
        return minutes < 1 ? "Just now" : Format.Duration(minutes) + " ago";
    }

    /// <summary>The small widget's dial: progress ring with the percentage, a check when done, the printer when idle.</summary>
    private static FrameworkElement SmallDial(WidgetState w)
    {
        var s = w.Status;
        var grid = new Grid();
        void Ring(double progress, Color tint) => grid.Children.Add(new ProgressRing(progress, tint, 6.5));
        UIElement Centre(UIElement e) { ((FrameworkElement)e).HorizontalAlignment = HorizontalAlignment.Center; ((FrameworkElement)e).VerticalAlignment = VerticalAlignment.Center; return e; }
        switch (s?.State)
        {
            case JobState.Printing or JobState.Paused:
                Ring(w.Progress, w.Tint);
                var percent = Row(0, Rounded(w.PercentText, 17, Bold), Text("%", 9, Bold, Theme.Secondary, display: true).WithMargin(0, 0, 0, 2).Aligned(vertical: VerticalAlignment.Bottom));
                percent.HorizontalAlignment = HorizontalAlignment.Center;
                grid.Children.Add(Centre(Column(-2, percent, s.State == JobState.Paused ? Icons.Make(Icon.Pause, 8, Theme.Brush(Palette.Paused)) : null)));
                break;
            case JobState.Preparing:
                Ring(s.Nozzle.Target > 0 ? Math.Min(1, s.Nozzle.Current / s.Nozzle.Target) : 0.1, Palette.Preparing);
                grid.Children.Add(Centre(Icons.Make(Icon.Flame, 18, Theme.Brush(Palette.Nozzle))));
                break;
            case JobState.Finished:
                Ring(1, Palette.Finished);
                grid.Children.Add(Centre(Icons.Make(Icon.Check, 20, Theme.Brush(Palette.Finished))));
                break;
            case JobState.Failed:
                var canceled = s.Canceled;
                Ring(w.Progress, canceled ? Theme.PrimaryColor.WithOpacity(0.6) : Palette.Failed);
                grid.Children.Add(Centre(canceled
                    ? Icons.Make(Icon.Close, 17, Theme.Secondary)
                    : Text("!", 22, Bold, Theme.Brush(Palette.Failed), display: true)));
                break;
            default:
                grid.Children.Add(new PrinterArtView(PrinterArt.For(s, w.Snapshot.Model, dimmed: !w.IsLive, animated: false), 54, 54) { Margin = new Thickness(2) });
                break;
        }
        return grid;
    }

    // MARK: - Medium

    public static UIElement MediumView(SharedSnapshot snapshot, DateTime date, bool showsTemps = true)
    {
        using var dark = Theme.Override(true);
        var w = new WidgetState(snapshot, date);
        var header = Row(4, Grow(Text(snapshot.PrinterName, 11.5, Semibold, Theme.Secondary)),
            w.IsLive ? Dot(w.StateColor, 6) : null,
            w.IsLive ? Text(w.StateTitle, 11, Semibold, Theme.Brush(w.StateColor)) : null);
        var rows = new List<UIElement?> { header, Text(w.Headline, 15, Semibold).WithMargin(0, 3, 0, 0) };
        if (w.IsLive && w.State.IsActive())
        {
            var big = Row(1, Rounded(w.PercentText, 30), Text("%", 15, Semibold, Theme.Secondary, display: true).WithMargin(0, 0, 0, 5).Aligned(vertical: VerticalAlignment.Bottom),
                Spacer(),
                w.Status is { TotalLayers: > 0 } st ? Text($"{st.Layer}/{st.TotalLayers}", 11, Medium, Theme.Secondary, display: true, digits: true).WithMargin(0, 0, 0, 6).Aligned(vertical: VerticalAlignment.Bottom) : null);
            rows.Add(big);
            rows.Add(FilamentProgressBar(w.Progress, w.Tint, 6).WithMargin(0, 5, 0, 5));
            rows.Add(Row(4, Grow(Text(w.StateLine, 11, Medium)), w.FinishText is { } f ? Text($"Done {f}", 11, Medium, Theme.Secondary) : null));
        }
        else
        {
            rows.Add(Lines(Text(w.StateLine, 11.5, color: Theme.Secondary, wrap: true), 2).WithMargin(0, 2, 0, 6));
            if (w.Status is { } s) rows.Add(SpoolRow(s, 22));
        }
        var column = Column(0, [.. rows]);
        column.VerticalAlignment = VerticalAlignment.Top;
        var right = new Grid();
        right.Children.Add(column);
        if (showsTemps && w.Status is { } live && live.State.IsActive())
        {
            var temps = Temps(live);
            temps.VerticalAlignment = VerticalAlignment.Bottom;
            right.Children.Add(temps);
        }
        var art = new PrinterArtView(PrinterArt.For(w.Status, snapshot.Model, dimmed: !w.IsLive, animated: false), 92, 110);
        return Row(14, art, Grow(right));
    }

    private static FrameworkElement Temps(PrinterStatus s)
    {
        FrameworkElement Item(Icon icon, double value, double target, Color tint) =>
            Row(3, Icons.Make(icon, 9, target > 0 ? Theme.Brush(tint) : Theme.Secondary), Text(Format.Temperature(value), 11, Medium, display: true, digits: true));
        return Row(12, Item(Icon.Flame, s.Nozzle.Current, s.Nozzle.Target, Palette.Nozzle), Item(Icon.Heat, s.Bed.Current, s.Bed.Target, Palette.Bed),
            s.Chamber is double c ? Item(Icon.Thermometer, c, 0, Palette.Chamber) : null).Aligned(HorizontalAlignment.Left);
    }

    private static FrameworkElement SpoolRow(PrinterStatus s, double size) => Row(6,
        [.. s.AllSpools.Where(sp => sp.IsLoaded || !sp.IsExternal).Take(8)
            .Select(sp => new SpoolSwatch(sp, sp.Id == s.ActiveSpoolId && s.State.IsActive(), size))]).Aligned(HorizontalAlignment.Left);

    // MARK: - Large

    public static UIElement LargeView(SharedSnapshot snapshot, DateTime date)
    {
        using var dark = Theme.Override(true);
        var w = new WidgetState(snapshot, date);
        var top = new Border { Height = 118, Child = MediumView(snapshot, date, showsTemps: false) };
        var rows = new List<UIElement?> { top };
        FrameworkElement? footer = null;
        if (w.Status is { } s)
        {
            var tiles = new List<UIElement?>
            {
                TempTile("Nozzle", Icon.Flame, s.Nozzle.Current, s.Nozzle.Target, Palette.Nozzle),
                TempTile("Bed", Icon.Heat, s.Bed.Current, s.Bed.Target, Palette.Bed),
            };
            if (s.Chamber is double chamber) tiles.Add(TempTile("Chamber", Icon.Thermometer, chamber, s.ChamberTarget, Palette.Chamber));
            else if (s.SecondNozzle is { } left) tiles.Add(TempTile("Left", Icon.Flame, left.Current, left.Target, Palette.Nozzle));
            else if (s.PartFan is int fan) tiles.Add(StatTile("Part fan", Icon.Fan, $"{fan}%", s.Speed.Title() + " speed", Palette.Chamber, fan > 0));
            rows.Add(Row(8, [.. tiles.Select(t => t is null ? null : Grow(t))]).WithMargin(0, 14, 0, 0));
            if (s.Ams.FirstOrDefault() is { } unit)
            {
                var spools = new Flex { Orientation = Orientation.Horizontal };
                foreach (var spool in unit.Spools)
                {
                    var cell = Column(3,
                        new SpoolSwatch(spool, spool.Id == s.ActiveSpoolId && s.State.IsActive(), 34) { HorizontalAlignment = HorizontalAlignment.Center },
                        Text(spool.IsLoaded ? spool.Material : "Empty", 10.5, Semibold).Aligned(HorizontalAlignment.Center),
                        Text(spool.IsLoaded ? spool.AmountText : spool.Label, 10, color: Theme.Secondary).Aligned(HorizontalAlignment.Center));
                    spools.Children.Add(Grow(cell));
                }
                rows.Add(Column(9,
                    Row(4, Grow(Text(unit.Title.ToUpperInvariant(), 10, Bold, Theme.Secondary)),
                        unit.Humidity is int h ? TightLabel(Icon.Drop, $"{h}%", 10.5, Theme.Secondary) : null),
                    spools).WithMargin(0, 14, 0, 0));
            }
            footer = LargeFooter(w, s, snapshot);
        }
        else
        {
            var note = Row(8,
                Icons.Make(snapshot.Link == SharedSnapshot.LinkState.Unconfigured ? Icon.Printer : Icon.WifiOff, 13, Theme.Secondary).Aligned(vertical: VerticalAlignment.Top),
                Grow(Text(snapshot.Link == SharedSnapshot.LinkState.Unconfigured
                    ? "Open Bambu Bar, pick your printer and enter its access code. Progress, temperatures and filament show up here."
                    : "Bambu Bar shows the printer here again as soon as it's back on the network.", 11.5, color: Theme.Secondary, wrap: true)));
            footer = new Border { Child = note, Padding = new Thickness(12), CornerRadius = new CornerRadius(12), Background = Theme.Fill(0.07) };
        }
        var grid = new Grid();
        var column = Column(0, [.. rows]);
        column.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(column);
        if (footer is not null)
        {
            footer.VerticalAlignment = VerticalAlignment.Bottom;
            grid.Children.Add(footer);
        }
        return grid;
    }

    private static FrameworkElement? LargeFooter(WidgetState w, PrinterStatus s, SharedSnapshot snapshot)
    {
        if (s.MostSevereIssue is { } issue)
            return Row(6, Icons.Make(Icon.Warning, 11, Theme.Brush(Palette.Paused)).Aligned(vertical: VerticalAlignment.Top),
                Grow(Lines(Text(issue.Message, 11, Medium, wrap: true), 2)));
        if (snapshot.LastJob is { } last)
        {
            var ok = last.Outcome == JobOutcome.Finished;
            return Row(6, Icons.Make(ok ? Icon.CheckCircle : Icon.XCircle, 11, Theme.Brush(ok ? Palette.Finished : Palette.Failed)),
                Grow(Text($"Last: {last.Name}", 11, Medium)),
                last.EndedAt is { } end ? Text(Format.Relative(end, w.Date), 11, Medium, Theme.Secondary) : null);
        }
        return null;
    }

    private static FrameworkElement TempTile(string title, Icon icon, double? value, double? target, Color tint) => Tile(
        Row(3, Icons.Make(icon, 9, (target ?? 0) > 0 ? Theme.Brush(tint) : Theme.Secondary), Text(title, 10, Medium, Theme.Secondary)),
        Rounded(value is double v ? Format.Temperature(v) : "–", 18),
        Text(target is double t ? t > 0 ? $"→ {(int)t}°" : "Off" : "Read only", 9.5, Medium, Theme.Tertiary));

    /// <summary>A tile like the temperature ones for a plain reading (fan speed on models without a chamber sensor).</summary>
    private static FrameworkElement StatTile(string title, Icon icon, string value, string detail, Color tint, bool active) => Tile(
        Row(3, Icons.Make(icon, 9, active ? Theme.Brush(tint) : Theme.Secondary), Text(title, 10, Medium, Theme.Secondary)),
        Rounded(value, 18),
        Text(detail, 9.5, Medium, Theme.Tertiary));

    private static FrameworkElement Tile(params UIElement[] rows) => new Border
    {
        Child = Column(2, rows), Padding = new Thickness(9, 7, 9, 7), CornerRadius = new CornerRadius(11), Background = Theme.Fill(0.07),
    };

    // MARK: - Helpers

    /// <summary>Shrinks a figure that doesn't fit, like SwiftUI's minimumScaleFactor.</summary>
    private static FrameworkElement Fit(TextBlock text) => new Viewbox
    {
        Child = text, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left,
    };

    /// <summary>At most <paramref name="lines"/> lines, the last one cut with an ellipsis.</summary>
    private static TextBlock Lines(TextBlock text, int lines)
    {
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.MaxHeight = text.FontSize * 1.34 * lines;
        return text;
    }
}
