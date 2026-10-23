using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BambuBar.Models;

namespace BambuBar.UI;

/// <summary>What a printer drawing shows: the model's shape, the print rising in its filament, the spools on top,
/// the chamber light. Shared by the rendered drawing and the 3D model.</summary>
public sealed record PrinterArt
{
    public JobState State { get; init; } = JobState.Idle;
    public double Progress { get; init; }
    public Color? Filament { get; set; }
    public ValueList<Color?> Spools { get; init; } = [];
    public int? ActiveSpool { get; init; }
    public bool LightOn { get; init; } = true;
    public bool DoorOpen { get; init; }
    public bool Dimmed { get; init; }
    /// <summary>Toolhead motion; off in widgets and lists.</summary>
    public bool Animated { get; init; } = true;
    public PrinterShape Shape { get; init; } = PrinterShape.Enclosed;

    /// <summary>The drawing for a status, with the first AMS unit on top.</summary>
    public static PrinterArt For(PrinterStatus? status, string? model = null, bool dimmed = false, bool animated = true, bool? lightOverride = null)
    {
        var unit = status?.Ams.FirstOrDefault();
        var active = status?.ActiveSpool;
        int? activeIndex = null;
        if (unit is not null && status is not null)
        {
            var index = unit.Spools.FindIndex(s => s.Id == status.ActiveSpoolId);
            if (index >= 0) activeIndex = index;
        }
        return new PrinterArt
        {
            State = status?.State ?? JobState.Idle,
            Progress = (status?.Progress ?? 0) / 100.0,
            Filament = active is not null ? Palette.Filament(active.Color) : null,
            Spools = [.. unit?.Spools.Select(s => s.IsLoaded ? Palette.Filament(s.Color) : null) ?? []],
            ActiveSpool = activeIndex,
            LightOn = lightOverride ?? status?.ChamberLightOn ?? false,
            DoorOpen = status?.DoorOpen ?? false,
            Dimmed = dimmed || status is null,
            Animated = animated,
            Shape = PrinterShapes.FromModel(model),
        };
    }

    /// <summary>How much of the print stands on the plate.</summary>
    public double ShownFraction => State switch
    {
        JobState.Finished => 1,
        JobState.Idle or JobState.Failed => Math.Min(1, Progress),
        JobState.Preparing => Progress > 0 ? Math.Max(0.03, Progress) : 0,
        // Something is always on the plate once printing starts.
        _ => Math.Max(0.03, Math.Min(1, Progress)),
    };
}

/// <summary>The Blender renders of one printer shape (from the macOS app's <c>artwork/generate_printers.py</c>), loaded once.</summary>
public sealed class PrinterRenderSet
{
    public sealed record Box(double X, double Y, double W, double H);

    private sealed class Anchors
    {
        public double[] Size { get; set; } = [];
        public Box? Part { get; set; }
        public Box? Model { get; set; }
    }

    public BitmapSource Body { get; }
    public BitmapSource Dark { get; }
    /// <summary>Without the AMS, for printers that have none.</summary>
    public BitmapSource? Bare { get; }
    public BitmapSource? BareDark { get; }
    public BitmapSource Part { get; }
    public IReadOnlyList<BitmapSource> SpoolLayers { get; }
    public BitmapSource? Sheen { get; }
    public Size Canvas { get; }
    /// <summary>Where the finished print sits, in canvas pixels from the top left.</summary>
    public Box PartBox { get; }
    /// <summary>The whole model, for the contact shadow under it.</summary>
    public Box? ModelBox { get; }

    private PrinterRenderSet(string name, Anchors anchors, Func<string, BitmapSource?> image)
    {
        Body = image("body")!;
        Dark = image("dark")!;
        Bare = image("bare");
        BareDark = image("baredark");
        Part = image("part")!;
        SpoolLayers = Enumerable.Range(0, 4).Select(i => image($"spool{i}")).OfType<BitmapSource>().ToList();
        Sheen = image("sheen");
        Canvas = new Size(anchors.Size[0], anchors.Size[1]);
        PartBox = anchors.Part!;
        ModelBox = anchors.Model;
    }

    private static readonly Dictionary<string, PrinterRenderSet?> Cache = [];

    public static PrinterRenderSet? Named(string name)
    {
        if (Cache.TryGetValue(name, out var hit)) return hit;
        PrinterRenderSet? set = null;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Printers.{name}.json");
            var anchors = stream is null ? null : JsonSerializer.Deserialize<Anchors>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (anchors is { Size.Length: 2, Part: not null })
            {
                static BitmapSource? Load(string file)
                {
                    try
                    {
                        var image = new BitmapImage();
                        image.BeginInit();
                        image.UriSource = new Uri($"pack://application:,,,/Resources/Printers/{file}.png");
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.EndInit();
                        image.Freeze();
                        // Straight BGRA, for the CPU tinting below.
                        var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
                        converted.Freeze();
                        return converted;
                    }
                    catch (IOException) { return null; }
                }
                set = new PrinterRenderSet(name, anchors, suffix => Load($"{name}-{suffix}"));
            }
        }
        catch (Exception e) when (e is IOException or JsonException or NotSupportedException) { set = null; }
        Cache[name] = set;
        return set;
    }

    private readonly Dictionary<(BitmapSource, Color?, bool), BitmapSource> _adjusted = [];

    /// <summary><paramref name="image"/> multiplied by <paramref name="tint"/> inside its own alpha (shading kept, filament
    /// colour applied), and nearly greyscale when <paramref name="muted"/> (an offline printer). Done on the CPU, so it
    /// looks the same in the panel, the widgets and snapshots.</summary>
    public BitmapSource Adjusted(BitmapSource image, Color? tint, bool muted)
    {
        if (tint is null && !muted) return image;
        if (_adjusted.TryGetValue((image, tint, muted), out var hit)) return hit;
        var width = image.PixelWidth;
        var height = image.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        image.CopyPixels(pixels, stride, 0);
        double tr = (tint?.R ?? 255) / 255.0, tg = (tint?.G ?? 255) / 255.0, tb = (tint?.B ?? 255) / 255.0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Premultiplied BGRA: multiplying the colour channels keeps them within alpha.
            double b = pixels[i] * tb, g = pixels[i + 1] * tg, r = pixels[i + 2] * tr;
            if (muted)
            {
                // Saturation 0.1, like the macOS app's dimmed drawing.
                var luma = 0.299 * r + 0.587 * g + 0.114 * b;
                r = luma + (r - luma) * 0.1;
                g = luma + (g - luma) * 0.1;
                b = luma + (b - luma) * 0.1;
            }
            pixels[i] = (byte)Math.Clamp(b, 0, 255);
            pixels[i + 1] = (byte)Math.Clamp(g, 0, 255);
            pixels[i + 2] = (byte)Math.Clamp(r, 0, 255);
        }
        var result = BitmapSource.Create(width, height, image.DpiX, image.DpiY, PixelFormats.Pbgra32, null, pixels, stride);
        result.Freeze();
        if (_adjusted.Count > 256) _adjusted.Clear();
        _adjusted[(image, tint, muted)] = result;
        return result;
    }
}

/// <summary>The rendered printer: body with the chamber light on or off, the AMS spools tinted with their filament,
/// and the print revealed from the plate up as it progresses, in the active filament's colour.</summary>
public sealed class PrinterArtView : FrameworkElement
{
    private readonly PrinterArt _art;

    public PrinterArtView(PrinterArt art, double? width = null, double? height = null)
    {
        _art = art;
        if (width is { } w) Width = w;
        if (height is { } h) Height = h;
        Opacity = art.Dimmed ? 0.55 : 1;
        IsHitTestVisible = false;
    }

    protected override Size MeasureOverride(Size available)
    {
        if (PrinterRenderSet.Named(_art.Shape.AssetName()) is not { } renders) return new Size(0, 0);
        var aspect = renders.Canvas.Width / renders.Canvas.Height;
        if (double.IsInfinity(available.Width) && double.IsInfinity(available.Height)) return new Size(100 * aspect, 100);
        if (double.IsInfinity(available.Width)) return new Size(available.Height * aspect, available.Height);
        if (double.IsInfinity(available.Height)) return new Size(available.Width, available.Width / aspect);
        var scale = Math.Min(available.Width / renders.Canvas.Width, available.Height / renders.Canvas.Height);
        return new Size(renders.Canvas.Width * scale, renders.Canvas.Height * scale);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (PrinterRenderSet.Named(_art.Shape.AssetName()) is not { } renders) return;
        var scale = Math.Min(ActualWidth / renders.Canvas.Width, ActualHeight / renders.Canvas.Height);
        if (scale <= 0) return;
        var size = new Size(renders.Canvas.Width * scale, renders.Canvas.Height * scale);
        var origin = new Point((ActualWidth - size.Width) / 2, (ActualHeight - size.Height) / 2);
        var frame = new Rect(origin, size);
        var muted = _art.Dimmed;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);

        if (renders.ModelBox is { } box)
        {
            // Soft contact shadow where the printer meets the desk.
            var shadow = new RadialGradientBrush(
                [new GradientStop(Color.FromArgb(140, 0, 0, 0), 0), new GradientStop(Color.FromArgb(51, 0, 0, 0), 0.5), new GradientStop(Colors.Transparent, 1)]);
            var w = box.W * scale * 1.05;
            var h = box.W * scale * 0.16;
            var x = origin.X + (box.X - box.W * 0.025) * scale;
            var y = origin.Y + (box.Y + box.H) * scale - box.W * scale * 0.11;
            dc.DrawEllipse(shadow, null, new Point(x + w / 2, y + h / 2), w / 2, h / 2);
        }

        var lit = _art.LightOn && !_art.Dimmed;
        var body = _art.Spools.Count == 0 && renders.Bare is not null && renders.BareDark is not null
            ? (lit ? renders.Bare : renders.BareDark)
            : (lit ? renders.Body : renders.Dark);
        dc.DrawImage(renders.Adjusted(body, null, muted), frame);
        for (var i = 0; i < renders.SpoolLayers.Count; i++)
        {
            if (i < _art.Spools.Count && _art.Spools[i] is { } color) dc.DrawImage(renders.Adjusted(renders.SpoolLayers[i], color, muted), frame);
        }
        var shown = _art.ShownFraction;
        if (shown > 0)
        {
            var part = renders.PartBox;
            var top = origin.Y + (part.Y + part.H * (1 - shown)) * scale;
            dc.PushClip(new RectangleGeometry(new Rect(origin.X, top, size.Width, Math.Max(0, origin.Y + size.Height - top))));
            dc.DrawImage(renders.Adjusted(renders.Part, _art.Filament ?? Palette.Accent, muted), frame);
            dc.Pop();
        }
        if (renders.Sheen is { } sheen)
        {
            dc.PushOpacity(0.85);
            dc.DrawImage(renders.Adjusted(sheen, null, muted), frame);
            dc.Pop();
        }
    }
}

public static class PrinterShapes
{
    public static PrinterShape FromModel(string? model) => PrinterShapeExtensions.FromModel(model);
}
