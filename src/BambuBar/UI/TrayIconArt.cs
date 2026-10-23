using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BambuBar.Models;
using BambuBar.Platform;
using BambuBar.Services;

namespace BambuBar.UI;

/// <summary>
/// What the tray icon shows. It follows the tray printer (Settings), not whichever printer the panel happens to
/// show. The macOS menu bar puts text beside the glyph; a notification-area icon is one small square, so the
/// percentage or time left is drawn into the icon itself and the full line goes in the tooltip.
/// </summary>
public sealed record TrayIconState(JobState? State, int Step, bool Alert, bool Offline, string? Number, string Tooltip, bool Dark)
{
    private static PrinterStore Store => PrinterStore.Shared;

    public static TrayIconState Current()
    {
        var session = Store.MenuBarSession;
        var s = session?.Status;
        var online = session?.IsOnline ?? false;
        var active = online && (s?.State.IsActive() ?? false);
        var state = online ? (s?.Canceled == true ? JobState.Idle : s?.State) : null;
        var progress = (s?.Progress ?? 0) / 100.0;
        return new TrayIconState(
            state,
            (int)Math.Round(progress * 20),  // 5 % steps are plenty at this size
            online && (s?.MostSevereIssue?.Severity ?? IssueSeverity.Info) <= IssueSeverity.Serious,
            Store.IsConfigured && !online,
            active && s is not null ? IconFigure(s, Store.MenuBarStyle) : null,
            TooltipFor(session),
            Theme.TaskbarDark);
    }

    /// <summary>The text the macOS app shows beside its icon for the chosen style.</summary>
    public static string? Text(PrinterStatus s, MenuBarStyle style)
    {
        var pct = $"{s.Progress}%";
        var left = s.RemainingMinutes > 0 ? Format.Duration(s.RemainingMinutes) : null;
        return style switch
        {
            MenuBarStyle.Icon => null,
            MenuBarStyle.Percent => s.State == JobState.Paused ? "Paused" : pct,
            MenuBarStyle.Remaining => s.State == JobState.Paused ? "Paused" : left ?? pct,
            _ when s.State == JobState.Paused => $"{pct} · Paused",
            _ when s.State == JobState.Preparing => s.Progress > 0 ? pct : "Preparing",
            _ => left is null ? pct : $"{pct} · {left}",
        };
    }

    /// <summary>The figure drawn in the icon: the percentage, or the time left in its largest unit ("2h", "45m").
    /// "Both" keeps the printer glyph, with its part rising, and puts both figures in the tooltip.</summary>
    private static string? IconFigure(PrinterStatus s, MenuBarStyle style)
    {
        if (s.State is JobState.Paused) return null;  // the glyph's pause bars say it better
        switch (style)
        {
            case MenuBarStyle.Percent:
                return s.State == JobState.Preparing && s.Progress == 0 ? null : s.Progress.ToString(CultureInfo.InvariantCulture);
            case MenuBarStyle.Remaining:
                if (s.RemainingMinutes <= 0) return s.Progress.ToString(CultureInfo.InvariantCulture);
                return s.RemainingMinutes >= 60 ? $"{(int)Math.Round(s.RemainingMinutes / 60.0)}h" : $"{s.RemainingMinutes}m";
            default:
                return null;
        }
    }

    private static string TooltipFor(PrinterSession? session)
    {
        if (session is null) return "Bambu Bar · No printer set up";
        var name = session.Config.Name;
        if (session.Status is not { } s || !session.IsOnline) return $"{name} · {session.LinkSummary}";
        if (!s.State.IsActive()) return $"{name} · {s.StateTitle}";
        var text = Text(s, MenuBarStyle.Both);
        var job = s.JobName is { Length: > 0 } j ? $"\n{j}" : "";
        var finish = s.State == JobState.Printing && s.EstimatedEnd is { } end ? $"\nDone {Format.Finish(end)}" : "";
        return $"{name} · {text}{job}{finish}";
    }

    public string AccessibleName => Tooltip.Replace('\n', ' ');
}

/// <summary>Draws <see cref="TrayIconState"/> into an icon, white on a dark taskbar and black on a light one.</summary>
public static class TrayIconArt
{
    private static readonly Dictionary<(TrayIconState, int), BitmapSource> Cache = [];

    /// <summary>The icon as an HICON the caller owns, <paramref name="pixels"/> square.</summary>
    public static IntPtr Icon(TrayIconState state, int pixels)
    {
        var bitmap = Render(state, pixels);
        var stride = pixels * 4;
        var bgra = new byte[stride * pixels];
        bitmap.CopyPixels(bgra, stride, 0);
        return CreateIcon(bgra, pixels);
    }

    /// <summary>The drawing at a size, for the icon and for snapshots.</summary>
    public static BitmapSource Render(TrayIconState state, int pixels)
    {
        // The tooltip doesn't change the picture; leave it out of the cache key.
        var key = (state with { Tooltip = "" }, pixels);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        if (Cache.Count > 200) Cache.Clear();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(pixels / 18.0, pixels / 18.0));
            var ink = state.Dark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A);
            if (state.Number is { } number) DrawNumber(dc, number, state, ink, pixels);
            else DrawGlyph(dc, state, ink);
            dc.Pop();
        }
        var target = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        Cache[key] = target;
        return target;
    }

    /// <summary>The printer's outline, the part rising inside it with progress, a pause or alert mark when needed,
    /// and a dashed outline while offline (the macOS menu bar glyph).</summary>
    private static void DrawGlyph(DrawingContext dc, TrayIconState state, Color ink)
    {
        var brush = new SolidColorBrush(ink);
        var frame = new Rect(2.25, 1.75, 13.5, 14.5);
        var pen = new Pen(brush, 1.5);
        if (state.Offline) pen.DashStyle = new DashStyle([2.2 / 1.5, 1.6 / 1.5], 0);
        Geometry outline = new RectangleGeometry(frame, 2.6, 2.6).GetWidenedPathGeometry(pen);
        var badge = new Rect(11.2, 0.2, 6.6, 6.6);
        var showBadge = state.Alert && state.State != JobState.Failed && !state.Offline;
        // The badge is cut out of the outline, with a ring of clear space round it.
        if (showBadge) outline = Geometry.Combine(outline, new EllipseGeometry(Inflate(badge, 1.2)), GeometryCombineMode.Exclude, null);
        dc.DrawGeometry(brush, null, outline);
        if (state.Offline) return;

        // Gantry with the toolhead.
        const double gantryY = 5.2, plateY = 13.4;
        dc.DrawRectangle(brush, null, new Rect(frame.Left + 1, gantryY - 0.4, frame.Width - 2, 0.8));
        // Build plate.
        dc.DrawRoundedRectangle(brush, null, new Rect(frame.Left + 2.2, plateY, frame.Width - 4.4, 1.3), 0.6, 0.6);

        switch (state.State)
        {
            case JobState.Paused:
                foreach (var x in new[] { 6.6, 9.9 }) dc.DrawRoundedRectangle(brush, null, new Rect(x, 7.2, 1.6, 4.8), 0.8, 0.8);
                break;
            case JobState.Failed:
                dc.DrawRoundedRectangle(brush, null, new Rect(8.2, 6.6, 1.6, 4.4), 0.8, 0.8);
                dc.DrawEllipse(brush, null, new Point(9, 12.7), 0.9, 0.9);
                break;
            case JobState.Printing or JobState.Preparing or JobState.Finished:
                var maxH = plateY - gantryY - 2.6;
                var h = state.State == JobState.Finished ? maxH : Math.Max(0.9, maxH * state.Step / 20.0);
                dc.DrawRoundedRectangle(brush, null, new Rect(6.2, plateY - h, 5.6, h), 0.9, 0.9);
                if (state.State != JobState.Finished)
                {
                    // Toolhead on top of the part.
                    var headY = Math.Max(gantryY + 0.6, plateY - h - 2.3);
                    dc.DrawRoundedRectangle(brush, null, new Rect(7.3, headY, 3.4, 1.8), 0.5, 0.5);
                }
                break;
            default:
                dc.DrawRoundedRectangle(brush, null, new Rect(frame.Left + 1.6, gantryY + 0.6, 3.2, 1.8), 0.5, 0.5);
                break;
        }
        if (showBadge) DrawBadge(dc, badge, Palette.Paused);
    }

    /// <summary>The percentage or time left, as large as fits, over a bar of the progress.</summary>
    private static void DrawNumber(DrawingContext dc, string number, TrayIconState state, Color ink, int pixels)
    {
        var brush = new SolidColorBrush(ink);
        var typeface = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Condensed);
        // Three characters ("100", "12h") need a narrower size than two.
        var size = number.Length >= 3 ? 9.6 : 12.4;
        var text = new FormattedText(number, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush, pixels / 18.0);
        // Shrink until it fits the width, for fonts wider than expected.
        while (text.WidthIncludingTrailingWhitespace > 17.4 && size > 6) { size -= 0.4; text.SetFontSize(size); }
        // Centre the ink, not the line box, in the space above the bar.
        var glyphs = text.BuildGeometry(new Point(0, 0));
        var bounds = glyphs.Bounds;
        glyphs.Transform = new TranslateTransform(9 - (bounds.Left + bounds.Width / 2), 7.1 - (bounds.Top + bounds.Height / 2));
        dc.DrawGeometry(brush, null, glyphs);
        // The bar along the bottom: a track, and the part done.
        var track = new Rect(1.5, 15.2, 15, 2);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x55, ink.R, ink.G, ink.B)), null, track, 1, 1);
        var done = track with { Width = Math.Max(2, track.Width * state.Step / 20.0) };
        dc.DrawRoundedRectangle(brush, null, done, 1, 1);
        if (state.Alert) DrawBadge(dc, new Rect(12.2, 0, 5.8, 5.8), Palette.Paused);
    }

    /// <summary>A filled circle with an exclamation mark cut out of it.</summary>
    private static void DrawBadge(DrawingContext dc, Rect badge, Color color)
    {
        var center = new Point(badge.Left + badge.Width / 2, badge.Top + badge.Height / 2);
        Geometry disc = new EllipseGeometry(badge);
        var unit = badge.Width / 6.6;
        var bar = new RectangleGeometry(new Rect(center.X - 0.6 * unit, badge.Top + 1.3 * unit, 1.2 * unit, 2.6 * unit), 0.6 * unit, 0.6 * unit);
        var dot = new EllipseGeometry(new Point(center.X, badge.Top + 5.05 * unit), 0.65 * unit, 0.65 * unit);
        disc = Geometry.Combine(disc, bar, GeometryCombineMode.Exclude, null);
        disc = Geometry.Combine(disc, dot, GeometryCombineMode.Exclude, null);
        dc.DrawGeometry(new SolidColorBrush(color), null, disc);
    }

    private static Rect Inflate(Rect r, double by) => new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);

    /// <summary>A 32-bit icon with alpha from premultiplied BGRA rows, top first.</summary>
    private static IntPtr CreateIcon(byte[] premultiplied, int pixels)
    {
        // Icons take straight alpha.
        for (var i = 0; i < premultiplied.Length; i += 4)
        {
            var a = premultiplied[i + 3];
            if (a is 0 or 255) continue;
            premultiplied[i] = (byte)Math.Min(255, premultiplied[i] * 255 / a);
            premultiplied[i + 1] = (byte)Math.Min(255, premultiplied[i + 1] * 255 / a);
            premultiplied[i + 2] = (byte)Math.Min(255, premultiplied[i + 2] * 255 / a);
        }
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = pixels,
            biHeight = -pixels,  // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var color = Native.CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        Marshal.Copy(premultiplied, 0, bits, premultiplied.Length);
        // The mask is ignored for 32-bit icons, but has to exist.
        var mask = Native.CreateBitmap(pixels, pixels, 1, 1, new byte[(pixels + 15) / 16 * 2 * pixels]);
        var info = new Native.ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        var icon = Native.CreateIconIndirect(ref info);
        Native.DeleteObject(color);
        Native.DeleteObject(mask);
        return icon;
    }
}
