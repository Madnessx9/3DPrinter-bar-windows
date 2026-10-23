using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace BambuBar.UI;

/// <summary>Small builders so views read like the SwiftUI they're ported from.</summary>
public static class Ui
{
    // MARK: - Text

    public static TextBlock Text(string text, double size = 12.5, FontWeight? weight = null, Brush? color = null,
        bool wrap = false, bool display = false, bool mono = false, bool digits = false, TextTrimming trim = TextTrimming.CharacterEllipsis)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            Foreground = color ?? Theme.Primary,
            FontFamily = mono ? Theme.MonoFont : display ? Theme.DisplayFont : Theme.UiFont,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : trim,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (digits) Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
        return block;
    }

    /// <summary>Text with <c>**bold**</c> runs, like SwiftUI's markdown strings.</summary>
    public static TextBlock Rich(string markdown, double size = 12.5, Brush? color = null, bool wrap = true)
    {
        var block = Text("", size, color: color, wrap: wrap);
        block.Text = null;
        var parts = markdown.Split("**");
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            block.Inlines.Add(new System.Windows.Documents.Run(parts[i])
            {
                FontWeight = i % 2 == 1 ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = i % 2 == 1 ? Theme.Primary : color ?? Theme.Primary,
            });
        }
        return block;
    }

    public static FontWeight Medium => FontWeights.Medium;
    public static FontWeight Semibold => FontWeights.SemiBold;
    public static FontWeight Bold => FontWeights.Bold;

    // MARK: - Stacks

    public static Flex Row(double spacing, params UIElement?[] children) => Stack(Orientation.Horizontal, spacing, children);
    public static Flex Column(double spacing, params UIElement?[] children) => Stack(Orientation.Vertical, spacing, children);

    private static Flex Stack(Orientation orientation, double spacing, UIElement?[] children)
    {
        var flex = new Flex { Orientation = orientation, Spacing = spacing };
        foreach (var child in children)
        {
            if (child is null) continue;
            Reuse.Detach(child);
            flex.Children.Add(child);
        }
        return flex;
    }

    /// <summary>Takes the free space in a row or column.</summary>
    public static FrameworkElement Spacer(double min = 0) => Grow(new Border { MinWidth = min, MinHeight = 0 });

    public static T Grow<T>(T element, double weight = 1) where T : UIElement
    {
        Flex.SetGrow(element, weight);
        return element;
    }

    public static T WithMargin<T>(this T element, double left, double top, double right, double bottom) where T : FrameworkElement
    {
        element.Margin = new Thickness(left, top, right, bottom);
        return element;
    }

    public static T WithMargin<T>(this T element, double all) where T : FrameworkElement
    {
        element.Margin = new Thickness(all);
        return element;
    }

    public static T Aligned<T>(this T element, HorizontalAlignment? horizontal = null, VerticalAlignment? vertical = null) where T : FrameworkElement
    {
        if (horizontal is { } h) element.HorizontalAlignment = h;
        if (vertical is { } v) element.VerticalAlignment = v;
        return element;
    }

    public static T Sized<T>(this T element, double? width = null, double? height = null) where T : FrameworkElement
    {
        if (width is { } w) element.Width = w;
        if (height is { } h) element.Height = h;
        return element;
    }

    public static T Tip<T>(this T element, string? tip) where T : FrameworkElement
    {
        if (!string.IsNullOrEmpty(tip))
        {
            element.ToolTip = tip;
            ToolTipService.SetInitialShowDelay(element, 500);
        }
        return element;
    }

    public static T Fade<T>(this T element, double opacity) where T : UIElement
    {
        element.Opacity = opacity;
        return element;
    }

    public static Border Pad(UIElement child, double left, double top, double right, double bottom) =>
        new() { Child = Reuse.Place(child), Padding = new Thickness(left, top, right, bottom) };

    public static Border Pad(UIElement child, double all) => Pad(child, all, all, all, all);

    public static Grid Overlay(params UIElement?[] layers)
    {
        var grid = new Grid();
        foreach (var layer in layers)
        {
            if (layer is null) continue;
            Reuse.Detach(layer);
            grid.Children.Add(layer);
        }
        return grid;
    }

    public static FrameworkElement Divider(bool vertical = false, double length = double.NaN) => new Rectangle
    {
        Fill = Theme.Fill(0.12),
        Width = vertical ? 1 : length,
        Height = vertical ? length : 1,
        HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Center,
        SnapsToDevicePixels = true,
    };

    public static Ellipse Dot(Color color, double size = 6, bool glow = false)
    {
        var dot = new Ellipse { Width = size, Height = size, Fill = Theme.Brush(color), VerticalAlignment = VerticalAlignment.Center };
        if (glow) dot.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = color, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.8 };
        return dot;
    }

    // MARK: - Panels

    /// <summary>A rounded module like Control Center's: a faint fill and hairline that sit on the backdrop in light
    /// and dark.</summary>
    public static Border Panel(UIElement content, double radius = 18, double padding = 14, Color? tint = null)
    {
        var top = Theme.PrimaryColor.WithOpacity(0.10);
        var bottom = Theme.PrimaryColor.WithOpacity(0.03);
        var border = new LinearGradientBrush(top, bottom, 90);
        border.Freeze();
        return new Border
        {
            Child = Reuse.Place(content),
            CornerRadius = new CornerRadius(radius),
            Padding = new Thickness(padding),
            Background = tint is { } t ? Theme.Brush(t, 0.12) : Theme.Fill(0.05),
            BorderBrush = border,
            BorderThickness = new Thickness(0.75),
        };
    }

    /// <summary>Small uppercase heading above a group.</summary>
    public static FrameworkElement SectionLabel(string title, string? trailing = null)
    {
        var label = Text(title.ToUpperInvariant(), 10.5, Semibold, Theme.Secondary);
        label.Margin = new Thickness(0, 0, 0, 0);
        TextOptions.SetTextFormattingMode(label, TextFormattingMode.Ideal);
        // Tracking 0.6 pt: WPF has no letter spacing, so the label stays as is.
        return Row(6, label, Spacer(6), trailing is null ? null : Text(trailing, 11, color: Theme.Tertiary));
    }

    /// <summary>Icon plus label.</summary>
    public static Flex Label(Icon icon, string text, double size = 12, FontWeight? weight = null, Brush? color = null, Brush? iconColor = null,
        double iconSize = 0)
    {
        return Row(4,
            Icons.Make(icon, iconSize > 0 ? iconSize : size * 0.85, iconColor ?? Theme.Secondary),
            Text(text, size, weight, color));
    }

    // MARK: - Buttons

    /// <summary>A button that draws itself through <paramref name="style"/> whenever it's hovered, pressed or disabled.</summary>
    public static Tap Button(UIElement content, Action onClick, Action<Tap>? style = null, string? tip = null)
    {
        var tap = new Tap(style) { Content = Reuse.Place(content) };
        tap.Click += (_, _) => onClick();
        tap.Tip(tip);
        return tap;
    }

    /// <summary>The panel's round glass-like control (Pause, Stop, Resume, Connect).</summary>
    public static Tap ControlButton(string title, Icon? icon, Action onClick, Color? tint = null, bool prominent = false, string? tip = null,
        double height = 34)
    {
        var fg = prominent ? Brushes.White : tint is { } t ? Theme.Brush(t) : Theme.Primary;
        var label = Row(6, icon is { } i ? Icons.Make(i, 11, fg) : null, Text(title, 12, Semibold, fg));
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        var body = new Border { Child = label, CornerRadius = new CornerRadius(11), MinHeight = height, BorderThickness = new Thickness(prominent ? 0 : 0.75) };
        return Button(body, onClick, tap =>
        {
            var pressed = tap.IsPressed;
            if (prominent)
            {
                var c = tint ?? Palette.AccentDeep;
                var gradient = new LinearGradientBrush(Lighten(c, pressed ? 0 : 0.12), c, 90);
                gradient.Freeze();
                body.Background = gradient;
            }
            else if (tint is { } t)
            {
                body.Background = Theme.Brush(t, pressed ? 0.22 : tap.IsMouseOver ? 0.17 : 0.12);
            }
            else
            {
                body.Background = Theme.Fill(pressed ? 0.12 : tap.IsMouseOver ? 0.09 : 0.06);
            }
            body.BorderBrush = Theme.Fill(0.07);
            tap.Opacity = tap.IsEnabled ? 1 : 0.45;
            tap.RenderTransformOrigin = new Point(0.5, 0.5);
            tap.RenderTransform = pressed ? new ScaleTransform(0.97, 0.97) : Transform.Identity;
        }, tip);
    }

    /// <summary>A round icon button in the panel's header.</summary>
    public static Tap HeaderIcon(UIElement icon, Action onClick, string tip)
    {
        var body = new Border { Child = Reuse.Place(icon), Width = 28, Height = 28, CornerRadius = new CornerRadius(14) };
        return Button(body, onClick, tap =>
        {
            body.Background = Theme.Fill(tap.IsPressed ? 0.14 : tap.IsMouseOver ? 0.10 : 0.06);
            tap.Opacity = tap.IsEnabled ? 1 : 0.45;
        }, tip);
    }

    public static Tap HeaderIcon(Icon icon, Action onClick, string tip) => HeaderIcon(Icons.Make(icon, 12, Theme.Secondary), onClick, tip);

    /// <summary>A flat text button (footer, "Later", links).</summary>
    public static Tap Plain(UIElement content, Action onClick, string? tip = null, double radius = 8, Thickness? padding = null)
    {
        var body = new Border { Child = Reuse.Place(content), CornerRadius = new CornerRadius(radius), Padding = padding ?? new Thickness(10, 6, 10, 6) };
        return Button(body, onClick, tap =>
        {
            body.Background = tap.IsPressed ? Theme.Fill(0.12) : tap.IsMouseOver ? Theme.Fill(0.07) : Brushes.Transparent;
            tap.Opacity = tap.IsEnabled ? 1 : 0.45;
        }, tip);
    }

    /// <summary>A standard push button (Fluent styled), for forms and settings.</summary>
    public static Button Push(string title, Action onClick, bool accent = false, bool small = false)
    {
        var button = new Button
        {
            Content = title,
            Padding = small ? new Thickness(10, 3, 10, 4) : new Thickness(14, 5, 14, 6),
            FontSize = small ? 12 : 13,
            MinWidth = small ? 0 : 72,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (accent)
        {
            button.Background = Theme.Brush(Palette.AccentDeep);
            button.Foreground = Brushes.White;
            button.BorderBrush = Theme.Brush(Palette.AccentDeep);
        }
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Tap Link(string title, Action onClick, Icon? icon = null, double size = 12.5, string? tip = null)
    {
        var accent = Theme.Brush(Palette.Accent);
        var content = Row(8, icon is { } i ? Icons.Make(i, size, accent).Sized(18) : null, Text(title, size, color: accent));
        return Plain(content, onClick, tip, 6, new Thickness(0, 2, 0, 2));
    }

    public static Color Lighten(Color c, double amount) => Color.FromArgb(c.A,
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));

    // MARK: - Animation

    /// <summary>Repeats an opacity pulse (the flame icon while heating).</summary>
    public static T Pulse<T>(this T element, bool active) where T : UIElement
    {
        if (!active) return element;
        var animation = new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        element.BeginAnimation(UIElement.OpacityProperty, animation);
        return element;
    }

    public static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { }
    }
}

/// <summary>A button with no chrome of its own: its content draws it, restyled on hover, press and enable changes.</summary>
public sealed class Tap : Button
{
    private static readonly ControlTemplate Bare = MakeTemplate();
    private readonly Action<Tap>? _style;

    public Tap(Action<Tap>? style)
    {
        _style = style;
        Template = Bare;
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        FocusVisualStyle = null;
        Focusable = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Loaded += (_, _) => Restyle();
    }

    private static ControlTemplate MakeTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(presenter);
        template.VisualTree = border;
        return template;
    }

    public void Restyle() => _style?.Invoke(this);

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsMouseOverProperty || e.Property == IsPressedProperty || e.Property == IsEnabledProperty) Restyle();
    }
}

/// <summary>An on/off switch like macOS's (Windows' toggle switch).</summary>
public sealed class Switch : ToggleButton
{
    private readonly Border _track = new() { Width = 40, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
    private readonly Ellipse _knob = new() { Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };

    public Switch(bool on, Action<bool> changed)
    {
        Template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        _track.Child = _knob;
        Content = _track;
        Cursor = Cursors.Hand;
        IsChecked = on;
        VerticalAlignment = VerticalAlignment.Center;
        Restyle();
        Checked += (_, _) => { Restyle(); changed(true); };
        Unchecked += (_, _) => { Restyle(); changed(false); };
        MouseEnter += (_, _) => Restyle();
        MouseLeave += (_, _) => Restyle();
    }

    private void Restyle()
    {
        var on = IsChecked == true;
        _track.Background = on ? Theme.Brush(Palette.AccentDeep) : IsMouseOver ? Theme.Fill(0.08) : Brushes.Transparent;
        _track.BorderBrush = on ? Theme.Brush(Palette.AccentDeep) : Theme.Secondary;
        _knob.Fill = on ? Brushes.White : Theme.Secondary;
        _knob.Margin = new Thickness(on ? 23 : 3, 0, 0, 0);
    }
}

/// <summary>An indeterminate spinner (SwiftUI's ProgressView).</summary>
public sealed class Spinner : Grid
{
    public Spinner(double size = 14, Brush? brush = null)
    {
        Width = size;
        Height = size;
        var arc = new Path
        {
            Data = Geometry.Parse("M 8,1.5 A 6.5,6.5 0 1 1 1.5,8"),
            Stroke = brush ?? Theme.Secondary,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.Uniform,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var rotate = new RotateTransform();
        arc.RenderTransform = rotate;
        Children.Add(arc);
        rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        VerticalAlignment = VerticalAlignment.Center;
    }
}
