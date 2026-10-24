using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BambuBar.UI;

/// <summary>The symbols the macOS app takes from SF Symbols: Windows' own icon font where it has a match
/// (Segoe Fluent Icons, Segoe MDL2 Assets on Windows 10), small drawings for the printer-specific ones.</summary>
public enum Icon
{
    Settings, Refresh, Printer, ChevronDown, ChevronLeft, ChevronRight, Grid, Play, Pause, Stop, Lock, Clock, Warning, Info,
    CheckCircle, XCircle, Check, Close, Lightbulb, Window, Download, Wifi, WifiOff, Key, Pencil, Copy, Code, Document,
    Feedback, Search, Power, Add, Speed, SpeedSilent, SpeedStandard, Bolt, Bell, Sliders, Sun, Moon, SdCard, Video, Pin, More, Folder,
    // Drawn
    Flame, Heat, Thermometer, Fan, Spool, Drop, Hand, Cube, Layers, Flag, Percent, Chip, Door, Nozzle, Widget, MenuBar,
}

public static class Icons
{
    private static readonly Dictionary<Icon, string> Glyphs = new()
    {
        [Icon.Settings] = "\uE713", [Icon.Refresh] = "\uE72C", [Icon.Printer] = "\uE749", [Icon.ChevronDown] = "\uE70D",
        [Icon.ChevronLeft] = "\uE76B", [Icon.ChevronRight] = "\uE76C", [Icon.Grid] = "\uF0E2", [Icon.Play] = "\uE768", [Icon.Pause] = "\uE769",
        [Icon.Stop] = "\uE71A", [Icon.Lock] = "\uE72E", [Icon.Clock] = "\uE823", [Icon.Warning] = "\uE7BA",
        [Icon.Info] = "\uE946", [Icon.CheckCircle] = "\uEC61", [Icon.XCircle] = "\uEB90", [Icon.Check] = "\uE73E",
        [Icon.Close] = "\uE711", [Icon.Lightbulb] = "\uEA80", [Icon.Window] = "\uE737", [Icon.Download] = "\uE896",
        [Icon.Wifi] = "\uE701", [Icon.WifiOff] = "\uEB55", [Icon.Key] = "\uE8D7", [Icon.Pencil] = "\uE70F",
        [Icon.Copy] = "\uE8C8", [Icon.Code] = "\uE943", [Icon.Document] = "\uE8A5", [Icon.Feedback] = "\uED15",
        [Icon.Search] = "\uE721", [Icon.Power] = "\uE7E8", [Icon.Add] = "\uE710", [Icon.Speed] = "\uEC4A", [Icon.SpeedSilent] = "\uEC48", [Icon.SpeedStandard] = "\uEC49",
        [Icon.Bolt] = "\uE945", [Icon.Bell] = "\uEA8F", [Icon.Sliders] = "\uE9E9", [Icon.Sun] = "\uE706",
        [Icon.Moon] = "\uE708", [Icon.SdCard] = "\uE7F1", [Icon.Video] = "\uE714", [Icon.Pin] = "\uE718",
        [Icon.More] = "\uE712", [Icon.Folder] = "\uE8B7",
    };

    /// <summary>16 × 16 drawings; <c>true</c> = filled, false = stroked.</summary>
    private static readonly Dictionary<Icon, (string Data, bool Fill)> Paths = new()
    {
        [Icon.Flame] = ("M8,1 C9.2,4 12.4,5.6 12.4,9.6 C12.4,12.7 10.4,15 8,15 C5.6,15 3.6,12.7 3.6,9.9 C3.6,8 4.6,6.7 5.8,5.9 C5.8,7.6 6.6,8.7 7.5,8.9 C6.9,6.4 7.1,3.5 8,1 Z", true),
        [Icon.Heat] = ("M4,2 C2.6,4 5.4,6 4,8 C2.6,10 5.4,12 4,14 M8,2 C6.6,4 9.4,6 8,8 C6.6,10 9.4,12 8,14 M12,2 C10.6,4 13.4,6 12,8 C10.6,10 13.4,12 12,14", false),
        [Icon.Thermometer] = ("M8,1.8 A1.7,1.7 0 0 1 9.7,3.5 V9.4 A3.1,3.1 0 1 1 6.3,9.4 V3.5 A1.7,1.7 0 0 1 8,1.8 Z M8,6.5 V11.6", false),
        [Icon.Spool] = ("M8,1 A7,7 0 1 1 7.99,1 Z M8,4 A4,4 0 1 0 8.01,4 Z M8,6.2 A1.8,1.8 0 1 1 7.99,6.2 Z", true),
        [Icon.Drop] = ("M8,1.5 C8,1.5 3.4,7 3.4,10.2 A4.6,4.6 0 0 0 12.6,10.2 C12.6,7 8,1.5 8,1.5 Z", true),
        [Icon.Hand] = ("M5,8 V3.6 A1,1 0 0 1 7,3.6 V7.4 V2.6 A1,1 0 0 1 9,2.6 V7.4 V3.6 A1,1 0 0 1 11,3.6 V8.2 V6.2 A1,1 0 0 1 13,6.2 V10 C13,13 11,15 8.6,15 C6.2,15 5.1,14 3.7,12.1 L2.2,9.6 A1,1 0 0 1 3.9,8.6 L5,10.2 Z", true),
        [Icon.Cube] = ("M8,1.5 L14,4.5 V11.5 L8,14.5 L2,11.5 V4.5 Z M2,4.5 L8,7.5 L14,4.5 M8,7.5 V14.5", false),
        [Icon.Layers] = ("M8,2 L14,5 L8,8 L2,5 Z M2,8 L8,11 L14,8 M2,11 L8,14 L14,11", false),
        [Icon.Flag] = ("M3,15 V1.8 M3,2 H13 L11,5.5 L13,9 H3", false),
        [Icon.Chip] = ("M4,4 H12 V12 H4 Z M6.5,6.5 H9.5 V9.5 H6.5 Z M6,1.5 V4 M10,1.5 V4 M6,12 V14.5 M10,12 V14.5 M1.5,6 H4 M1.5,10 H4 M12,6 H14.5 M12,10 H14.5", false),
        [Icon.Door] = ("M4,1.5 H12 V14.5 H4 Z M9.6,7.6 V8.6", false),
        [Icon.Nozzle] = ("M5,1.5 H11 V7.5 L8,14 L5,7.5 Z M5,5 H11", false),
        [Icon.Widget] = ("M2,2.5 H8 V8 H2 Z M10,2.5 H14 V13.5 H10 Z M2,10 H8 V13.5 H2 Z", false),
        [Icon.MenuBar] = ("M1.5,2.5 H14.5 V13.5 H1.5 Z M1.5,5.2 H14.5", false),
    };

    /// <summary>An icon <paramref name="size"/> device-independent pixels tall.</summary>
    public static FrameworkElement Make(Icon icon, double size, Brush brush)
    {
        if (icon == Icon.Fan) return Fan(size, brush);
        if (icon == Icon.Percent)
        {
            return new TextBlock
            {
                Text = "%", FontFamily = Theme.UiFont, FontWeight = FontWeights.Bold, FontSize = size * 1.05, Foreground = brush,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            };
        }
        if (Glyphs.TryGetValue(icon, out var glyph))
        {
            return new TextBlock
            {
                Text = glyph, FontFamily = Theme.IconFont, FontSize = size, Foreground = brush,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
        }
        var (data, fill) = Paths[icon];
        var path = new Path
        {
            Data = Geometry.Parse(data),
            Width = 16, Height = 16, Stretch = Stretch.None,
        };
        if (fill)
        {
            // Path markup fills even-odd, which cuts the spool's hub out of its ring.
            path.Fill = brush;
        }
        else
        {
            path.Stroke = brush;
            path.StrokeThickness = 1.4;
            path.StrokeLineJoin = PenLineJoin.Round;
            path.StrokeStartLineCap = PenLineCap.Round;
            path.StrokeEndLineCap = PenLineCap.Round;
        }
        return new Viewbox
        {
            Width = size, Height = size, Child = new Canvas { Width = 16, Height = 16, Children = { path } },
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    private static FrameworkElement Fan(double size, Brush brush)
    {
        var group = new GeometryGroup();
        for (var i = 0; i < 3; i++)
        {
            var blade = new EllipseGeometry(new Point(8, 4.4), 2.6, 3.9) { Transform = new RotateTransform(i * 120 + 18, 8, 8) };
            group.Children.Add(blade);
        }
        group.Children.Add(new EllipseGeometry(new Point(8, 8), 1.6, 1.6));
        var path = new Path { Data = group, Fill = brush, Width = 16, Height = 16 };
        return new Viewbox
        {
            Width = size, Height = size, Child = new Canvas { Width = 16, Height = 16, Children = { path } },
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
    }
}
