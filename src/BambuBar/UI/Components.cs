using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using BambuBar.Models;
using static BambuBar.UI.Ui;

namespace BambuBar.UI;

/// <summary>A filament spool seen from the side: the colour, a hub, and a ring showing how much is left.</summary>
public sealed class SpoolSwatch : FrameworkElement
{
    private readonly Spool? _spool;
    private readonly bool _active;
    private readonly double _size;
    /// <summary>Printed on a wider hub, such as the full spool's weight.</summary>
    private readonly string? _hubText;

    public SpoolSwatch(Spool? spool, bool active = false, double size = 34, string? hubText = null)
    {
        _spool = spool;
        _active = active;
        _size = size;
        _hubText = hubText;
        Width = size;
        Height = size;
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var s = _size;
        var ring = Math.Max(2, s * 0.075);
        var gap = Math.Max(1.5, s * 0.06);
        var center = new Point(s / 2, s / 2);
        var ringRadius = (s - ring) / 2;
        Color? color = _spool is { IsLoaded: true } loaded ? Palette.Filament(loaded.Color) : null;

        dc.DrawEllipse(null, new Pen(Theme.Fill(0.10), ring), center, ringRadius, ringRadius);
        if (_spool?.Remain is int remain && color is not null)
        {
            var pen = new Pen(_active ? Theme.Brush(Palette.Accent) : Theme.Fill(0.55), ring) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            DrawArc(dc, pen, center, ringRadius, Math.Clamp(remain, 0, 100) / 100.0);
        }
        else if (_active)
        {
            dc.DrawEllipse(null, new Pen(Theme.Brush(Palette.Accent), ring), center, ringRadius, ringRadius);
        }

        var inner = s / 2 - ring - gap;
        if (inner <= 0) return;
        if (color is { } c)
        {
            var colors = (_spool!.Colors ?? []).Select(Palette.Filament).OfType<Color>().ToList();
            if (colors.Count > 1)
            {
                // Multicolour filament: one wedge per colour (WPF has no angular gradient).
                for (var i = 0; i < colors.Count; i++)
                {
                    var start = i / (double)colors.Count * 2 * Math.PI - Math.PI / 2;
                    var end = (i + 1) / (double)colors.Count * 2 * Math.PI - Math.PI / 2;
                    var wedge = new StreamGeometry();
                    using (var g = wedge.Open())
                    {
                        g.BeginFigure(center, true, true);
                        g.LineTo(new Point(center.X + inner * Math.Cos(start), center.Y + inner * Math.Sin(start)), false, false);
                        g.ArcTo(new Point(center.X + inner * Math.Cos(end), center.Y + inner * Math.Sin(end)), new Size(inner, inner), 0,
                            end - start > Math.PI, SweepDirection.Clockwise, false, false);
                    }
                    dc.DrawGeometry(Theme.Brush(colors[i]), null, wedge);
                }
            }
            else
            {
                dc.DrawEllipse(Theme.Brush(c), null, center, inner, inner);
            }
            var shade = new LinearGradientBrush(
                [new GradientStop(Color.FromArgb(77, 255, 255, 255), 0), new GradientStop(Colors.Transparent, 0.5), new GradientStop(Color.FromArgb(56, 0, 0, 0), 1)],
                new Point(0, 0), new Point(1, 1));
            dc.DrawEllipse(shade, new Pen(Theme.Fill(BambuColor.Luminance(_spool.Color) > 0.85 ? 0.18 : 0.06), 0.75), center, inner, inner);
            if (_hubText is not null)
            {
                var hub = s * 0.29;
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(158, 0, 0, 0)), new Pen(new SolidColorBrush(Color.FromArgb(51, 255, 255, 255)), 0.5), center, hub, hub);
                var text = new FormattedText(_hubText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(Theme.DisplayFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), s * 0.18, Brushes.White, 1.0)
                {
                    MaxTextWidth = hub * 2 - s * 0.06, MaxLineCount = 1, Trimming = TextTrimming.None, TextAlignment = TextAlignment.Center,
                };
                // Shrink to fit, like minimumScaleFactor.
                if (text.WidthIncludingTrailingWhitespace > hub * 2 - s * 0.06) text.SetFontSize(s * 0.18 * 0.7);
                dc.DrawText(text, new Point(center.X - text.MaxTextWidth / 2, center.Y - text.Height / 2));
            }
            else
            {
                var hub = s * 0.1;
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), new Pen(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), 0.5), center, hub, hub);
            }
        }
        else
        {
            var dashed = new Pen(Theme.Fill(0.22), 1) { DashStyle = new DashStyle([2.5, 2.5], 0) };
            dc.DrawEllipse(null, dashed, center, inner - 0.5, inner - 0.5);
        }
    }

    /// <summary>An arc from 12 o'clock, clockwise, covering <paramref name="fraction"/> of the circle.</summary>
    public static void DrawArc(DrawingContext dc, Pen pen, Point center, double radius, double fraction)
    {
        if (fraction <= 0) return;
        if (fraction >= 0.999)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        var end = fraction * 2 * Math.PI - Math.PI / 2;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(center.X, center.Y - radius), false, false);
            g.ArcTo(new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end)), new Size(radius, radius), 0,
                fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}

/// <summary>A circular gauge for a single percentage, used by the small widget.</summary>
public sealed class ProgressRing : FrameworkElement
{
    private readonly double _progress;
    private readonly Color _tint;
    private readonly double _lineWidth;
    private readonly Brush? _track;

    public ProgressRing(double progress, Color tint, double lineWidth = 8, Brush? track = null)
    {
        _progress = Math.Clamp(progress, 0, 1);
        _tint = tint;
        _lineWidth = lineWidth;
        _track = track;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - _lineWidth) / 2;
        dc.DrawEllipse(null, new Pen(_track ?? Theme.Fill(0.10), _lineWidth), center, radius, radius);
        var gradient = new LinearGradientBrush(_tint.WithOpacity(0.55), _tint, new Point(0, 1), new Point(1, 0));
        var pen = new Pen(gradient, _lineWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        SpoolSwatch.DrawArc(dc, pen, center, radius, _progress);
    }
}

public static class Components
{
    /// <summary>A thin progress bar filled with the filament colour, with a soft glow along it.</summary>
    public static FrameworkElement FilamentProgressBar(double progress, Color tint, double height = 8)
    {
        progress = Math.Clamp(progress, 0, 1);
        var grid = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = Theme.Fill(0.09) });
        if (progress > 0)
        {
            var fill = new Border
            {
                CornerRadius = new CornerRadius(height / 2),
                Background = new LinearGradientBrush(tint.WithOpacity(0.75), tint, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Effect = new DropShadowEffect { Color = tint, Opacity = 0.45, BlurRadius = height * 1.2, ShadowDepth = 0 },
                Child = new Border
                {
                    CornerRadius = new CornerRadius(height * 0.175),
                    Background = new SolidColorBrush(Color.FromArgb(71, 255, 255, 255)),
                    Height = height * 0.35,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(height * 0.4, 1, height * 0.4, 0),
                },
            };
            grid.Children.Add(fill);
            // Width follows the track, so the bar keeps its proportion at any size.
            grid.SizeChanged += (_, e) => fill.Width = Math.Max(height, e.NewSize.Width * progress);
        }
        return grid;
    }

    /// <summary>"● Printing" capsule.</summary>
    public static FrameworkElement StatePill(JobState state, string? text = null, bool compact = false, Color? color = null)
    {
        var c = color ?? Palette.ColorFor(state);
        var content = Row(5, Dot(c, 6, glow: true), Text(text ?? state.Title(), compact ? 10.5 : 11.5, Semibold, Theme.Brush(c)));
        return new Border
        {
            Child = content,
            Padding = compact ? new Thickness(7, 3, 7, 3) : new Thickness(9, 4, 9, 4),
            CornerRadius = new CornerRadius(20),
            Background = Theme.Brush(c, 0.14),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    /// <summary>A row of label/value pairs for detail sections.</summary>
    public static FrameworkElement DetailRow(string title, string value, Icon? icon = null, double size = 12)
    {
        var valueText = Text(value, size, Medium, wrap: true);
        valueText.TextAlignment = TextAlignment.Right;
        valueText.HorizontalAlignment = HorizontalAlignment.Right;
        return Row(8,
            icon is { } i ? Icons.Make(i, 11, Theme.Secondary).Sized(16) : null,
            Text(title, size, color: Theme.Secondary),
            Grow(valueText));
    }

    /// <summary>Nozzle (two on dual-nozzle models, or T1–T4 on multi-toolhead models), bed, chamber, and X/Y stepper drivers.</summary>
    public static List<FrameworkElement> TemperatureTiles(PrinterStatus status, PrinterModel? model, bool includeToolheads = true)
    {
        var tiles = new List<FrameworkElement>();
        var nozzleMax = (double)(model?.NozzleMax ?? 300);
        if (includeToolheads)
        {
            if (status.ToolheadTemps.Count > 1)
            {
                for (var i = 0; i < status.ToolheadTemps.Count; i++)
                {
                    var isActive = status.ActiveSpoolId == i;
                    var label = isActive ? $"T{i + 1} · Active" : $"T{i + 1}";
                    tiles.Add(TemperatureTile(label, Icon.Flame, status.ToolheadTemps[i], null, Palette.Nozzle, nozzleMax));
                }
            }
            else if (status.SecondNozzle is { } left)
            {
                tiles.Add(TemperatureTile("Left", Icon.Flame, left, null, Palette.Nozzle, nozzleMax));
                tiles.Add(TemperatureTile("Right", Icon.Flame, status.Nozzle, null, Palette.Nozzle, nozzleMax));
            }
            else
            {
                tiles.Add(TemperatureTile("Nozzle", Icon.Flame, status.Nozzle, null, Palette.Nozzle, nozzleMax));
            }
        }
        tiles.Add(TemperatureTile("Bed", Icon.Heat, status.Bed, null, Palette.Bed, model?.BedMax ?? 110));
        if (status.Chamber is double chamber)
        {
            if (model?.ChamberHeaterMax is int heater)
                tiles.Add(TemperatureTile("Chamber", Icon.Thermometer, new Temperature(chamber, status.ChamberTarget ?? 0), null, Palette.Chamber, heater));
            else
                tiles.Add(TemperatureTile("Chamber", Icon.Thermometer, null, chamber, Palette.Chamber, 65));
        }
        else if (model?.HasChamberSensor ?? true)
        {
            // A model with a sensor that hasn't reported yet.
            tiles.Add(TemperatureTile("Chamber", Icon.Thermometer, null, null, Palette.Chamber, 65));
        }
        if (status.DriverXTemp is double dx)
            tiles.Add(TemperatureTile("X Driver", Icon.Chip, null, dx, Palette.Chamber, 120, "Stepper TMC"));
        if (status.DriverYTemp is double dy)
            tiles.Add(TemperatureTile("Y Driver", Icon.Chip, null, dy, Palette.Chamber, 120, "Stepper TMC"));
        return tiles;
    }

    /// <summary>Arranges temperature tiles cleanly in one row for 1–2 nozzle printers, or two rows (Toolheads T1–T4 on top,
    /// Bed/Chamber/Drivers/Fans below) for multi-toolhead printers like the Snapmaker U1.</summary>
    public static FrameworkElement TemperaturesLayout(PrinterStatus status, PrinterModel? model, bool includeFans, double spacing = 10)
    {
        if (status.ToolheadTemps.Count > 1)
        {
            var nozzleMax = (double)(model?.NozzleMax ?? 300);
            var toolheadRow = new List<FrameworkElement>();
            for (var i = 0; i < status.ToolheadTemps.Count; i++)
            {
                var isActive = status.ActiveSpoolId == i;
                var label = isActive ? $"T{i + 1} · Active" : $"T{i + 1}";
                toolheadRow.Add(TemperatureTile(label, Icon.Flame, status.ToolheadTemps[i], null, Palette.Nozzle, nozzleMax));
            }
            var secondRow = TemperatureTiles(status, model, includeToolheads: false);
            if (includeFans) secondRow.Add(FanTile(status));
            return Column(spacing,
                Row(spacing, [.. toolheadRow.Select(t => Grow(t))]),
                Row(spacing, [.. secondRow.Select(t => Grow(t))]));
        }

        var tiles = TemperatureTiles(status, model, includeToolheads: true);
        if (includeFans) tiles.Add(FanTile(status));
        return Row(spacing, [.. tiles.Select(t => Grow(t))]);
    }

    /// <summary>A temperature readout: value large, target small, a hairline gauge underneath.</summary>
    public static FrameworkElement TemperatureTile(string title, Icon icon, Temperature? temperature, double? plain, Color tint, double maxValue, string? plainLabel = null)
    {
        var current = temperature?.Current ?? plain;
        var heating = (temperature?.Target ?? 0) > 0;
        var rising = temperature is { Target: > 0 } t && t.Current < t.Target - 3;
        string targetText;
        if (temperature is not { } temp) targetText = plain is null ? "No sensor" : (plainLabel ?? "Read only");
        else if (temp.Target <= 0) targetText = "Off";
        else if (temp.IsAtTarget) targetText = $"At {(int)temp.Target}°";
        else targetText = temp.Current < temp.Target ? $"Heating to {(int)temp.Target}°" : $"Cooling to {(int)temp.Target}°";

        var value = Text(current is double v ? $"{(int)Math.Round(v, MidpointRounding.AwayFromZero)}°" : "–", 22, Semibold, display: true, digits: true);
        var gauge = new Grid { Height = 3 };
        gauge.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = Theme.Fill(0.08) });
        var fill = new Border { CornerRadius = new CornerRadius(1.5), Background = heating ? Theme.Brush(tint) : Theme.Fill(0.3), HorizontalAlignment = HorizontalAlignment.Left };
        gauge.Children.Add(fill);
        var marker = temperature is { Target: > 0 } ? new Border { Width = 1.5, Height = 7, CornerRadius = new CornerRadius(0.75), Background = Theme.Fill(0.6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, -2, 0, -2) } : null;
        if (marker is not null) gauge.Children.Add(marker);
        gauge.SizeChanged += (_, e) =>
        {
            var w = e.NewSize.Width;
            fill.Width = Math.Max(3, w * Math.Clamp((current ?? 0) / maxValue, 0, 1));
            if (marker is not null) marker.Margin = new Thickness(w * Math.Min(1, temperature!.Target / maxValue) - 0.75, -2, 0, -2);
        };
        var content = Column(6,
            Row(4, Icons.Make(icon, 10, heating ? Theme.Brush(tint) : Theme.Secondary).Pulse(rising), Text(title, 11, Medium, Theme.Secondary)),
            new Viewbox { Child = value, Stretch = Stretch.None, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left },
            Text(targetText, 10.5, Medium, Theme.Tertiary),
            gauge);
        var tile = Panel(content, 14, 11);
        tile.ToolTip = $"{title} {(current is double c ? $"{(int)c} degrees" : "unknown")}, {targetText}";
        return tile;
    }

    /// <summary>Part, aux, chamber and hotend fans as small bars.</summary>
    public static FrameworkElement FanTile(PrinterStatus status)
    {
        FrameworkElement Fan(string title, int? value)
        {
            var bar = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Theme.Fill(0.08) });
            var fill = new Border { CornerRadius = new CornerRadius(2), Background = Theme.Brush(Palette.Chamber, 0.85), HorizontalAlignment = HorizontalAlignment.Left };
            bar.Children.Add(fill);
            bar.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * Math.Clamp(value ?? 0, 0, 100) / 100.0;
            var percent = Text(value is int p ? $"{p}%" : "–", 10.5, Medium, digits: true);
            percent.TextAlignment = TextAlignment.Right;
            return Row(6, Text(title, 10.5, color: Theme.Secondary).Sized(46), Grow(bar), percent.Sized(32));
        }

        var rows = new List<UIElement?>
        {
            Row(4, Icons.Make(Icon.Fan, 10, (status.PartFan ?? 0) > 0 ? Theme.Brush(Palette.Chamber) : Theme.Secondary),
                Text("Fans", 11, Medium, Theme.Secondary)),
            Fan("Part", status.PartFan),
        };
        if (status.AuxFan is int aux) rows.Add(Fan("Aux", aux));
        if (status.ExhaustFan is int exhaust) rows.Add(Fan("Chamber", exhaust));
        if (status.HeatbreakFan is int hotend && (status.AuxFan is null || status.ExhaustFan is null)) rows.Add(Fan("Hotend", hotend));
        return Panel(Column(7, [.. rows]), 14, 11);
    }

    /// <summary>An icon and text in the panel's tight style (clock, "Ready to remove").</summary>
    public static FrameworkElement TightLabel(Icon icon, string text, double size, Brush? color = null, Brush? iconColor = null) =>
        Row(4, Icons.Make(icon, 10, iconColor ?? Theme.Secondary), Text(text, size, Medium, color));

    /// <summary>Relative time like "3 hr. ago", "yesterday".</summary>
    public static string RelativeTime(DateTime utc) => Format.Relative(utc);
}
