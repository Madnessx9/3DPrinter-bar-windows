using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BambuBar.Models;
using static BambuBar.UI.Ui;

namespace BambuBar.UI.Pages;

/// <summary>Pieces every page uses.</summary>
public static class PageKit
{
    /// <summary>Page heading: title, subtitle, and something on the right.</summary>
    public static FrameworkElement PageHeader(string title, string? subtitle = null, UIElement? trailing = null) =>
        PageHeader(Text(title, 26, Bold, display: true), subtitle, trailing);

    public static FrameworkElement PageHeader(UIElement title, string? subtitle, UIElement? trailing)
    {
        var left = Column(3, title, subtitle is null ? null : Text(subtitle, 13, color: Theme.Secondary));
        if (trailing is FrameworkElement t) t.VerticalAlignment = VerticalAlignment.Bottom;
        return Row(12, Grow(left), trailing).WithMargin(0, 0, 0, 4);
    }

    /// <summary>A big empty state in a panel.</summary>
    public static FrameworkElement Empty(Icon icon, string title, string detail)
    {
        var column = Column(8,
            Icons.Make(icon, 34, Theme.Tertiary).Aligned(HorizontalAlignment.Center),
            Text(title, 15, Semibold).Aligned(HorizontalAlignment.Center),
            Center(Text(detail, 12, color: Theme.Secondary, wrap: true)));
        return Panel(Pad(column, 0, 36, 0, 36));
    }

    public static TextBlock Center(TextBlock text)
    {
        text.TextAlignment = TextAlignment.Center;
        text.HorizontalAlignment = HorizontalAlignment.Center;
        return text;
    }

    /// <summary>A heading and its content.</summary>
    public static FrameworkElement Section(string title, params UIElement?[] content) => Column(8, [SectionLabel(title), .. content]);

    /// <summary>Two equal columns.</summary>
    public static FrameworkElement Columns(double spacing, params UIElement[] children)
    {
        var row = Row(spacing, [.. children.Select(c => Grow(c))]);
        return row;
    }

    /// <summary>A faint wash in the colour of the current state behind every page.</summary>
    public static Brush PageWash(PrinterStatus? status, bool top)
    {
        if (top)
        {
            var color = status is null ? Colors.Transparent : Palette.ColorFor(status.State);
            var brush = new RadialGradientBrush(color.WithOpacity(0.10), color.WithOpacity(0))
            {
                Center = new Point(0, 0), GradientOrigin = new Point(0, 0), RadiusX = 1.0, RadiusY = 1.1,
            };
            brush.Freeze();
            return brush;
        }
        var corner = new RadialGradientBrush(Palette.Accent.WithOpacity(0.05), Palette.Accent.WithOpacity(0))
        {
            Center = new Point(1, 1), GradientOrigin = new Point(1, 1), RadiusX = 0.8, RadiusY = 0.9,
        };
        corner.Freeze();
        return corner;
    }
}
