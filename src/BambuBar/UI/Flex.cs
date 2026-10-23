using System.Windows;
using System.Windows.Controls;

namespace BambuBar.UI;

/// <summary>
/// A row or column like SwiftUI's HStack and VStack: children in order with fixed spacing, cross-axis placement from
/// each child's alignment, and <see cref="GrowProperty">Grow</see> children (spacers, stretching labels) sharing
/// what's left along the main axis.
/// </summary>
public sealed class Flex : Panel
{
    public static readonly DependencyProperty GrowProperty = DependencyProperty.RegisterAttached(
        "Grow", typeof(double), typeof(Flex), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static double GetGrow(UIElement element) => (double)element.GetValue(GrowProperty);
    public static void SetGrow(UIElement element, double value) => element.SetValue(GrowProperty, value);

    public Orientation Orientation { get; set; } = Orientation.Vertical;
    public double Spacing { get; set; }

    private bool Horizontal => Orientation == Orientation.Horizontal;

    private static double Main(Size size, bool horizontal) => horizontal ? size.Width : size.Height;
    private static double Cross(Size size, bool horizontal) => horizontal ? size.Height : size.Width;
    private static Size Make(double main, double cross, bool horizontal) => horizontal ? new Size(main, cross) : new Size(cross, main);

    private IEnumerable<UIElement> Visible => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed);

    protected override Size MeasureOverride(Size available)
    {
        var horizontal = Horizontal;
        var visible = Visible.ToList();
        var mainAvailable = Main(available, horizontal);
        var crossAvailable = Cross(available, horizontal);
        double used = Spacing * Math.Max(0, visible.Count - 1), cross = 0, growTotal = 0;
        // Fixed children first, with whatever main-axis room there is.
        foreach (var child in visible)
        {
            var grow = GetGrow(child);
            if (grow > 0) { growTotal += grow; continue; }
            child.Measure(Make(double.IsInfinity(mainAvailable) ? double.PositiveInfinity : Math.Max(0, mainAvailable - used), crossAvailable, horizontal));
            used += Main(child.DesiredSize, horizontal);
            cross = Math.Max(cross, Cross(child.DesiredSize, horizontal));
        }
        // Growing children share the rest; with unbounded room they take only what they need.
        var rest = double.IsInfinity(mainAvailable) ? double.PositiveInfinity : Math.Max(0, mainAvailable - used);
        double growUsed = 0;
        foreach (var child in visible)
        {
            var grow = GetGrow(child);
            if (grow <= 0) continue;
            var share = double.IsInfinity(rest) ? double.PositiveInfinity : rest * grow / growTotal;
            child.Measure(Make(share, crossAvailable, horizontal));
            growUsed += Main(child.DesiredSize, horizontal);
            cross = Math.Max(cross, Cross(child.DesiredSize, horizontal));
        }
        return Make(used + growUsed, cross, horizontal);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var horizontal = Horizontal;
        var visible = Visible.ToList();
        var mainFinal = Main(final, horizontal);
        var crossFinal = Cross(final, horizontal);
        double fixedTotal = Spacing * Math.Max(0, visible.Count - 1), growTotal = 0;
        foreach (var child in visible)
        {
            var grow = GetGrow(child);
            if (grow > 0) growTotal += grow;
            else fixedTotal += Main(child.DesiredSize, horizontal);
        }
        var rest = Math.Max(0, mainFinal - fixedTotal);
        double position = 0;
        foreach (var child in visible)
        {
            var grow = GetGrow(child);
            var main = grow > 0 ? rest * grow / growTotal : Main(child.DesiredSize, horizontal);
            var childCross = Cross(child.DesiredSize, horizontal);
            var alignment = child is FrameworkElement fe ? (horizontal ? Align(fe.VerticalAlignment) : Align(fe.HorizontalAlignment)) : 0;
            double offset = 0, size = childCross;
            switch (alignment)
            {
                case 0: size = crossFinal; break;  // stretch
                case 1: break;  // start
                case 2: offset = (crossFinal - childCross) / 2; break;
                case 3: offset = crossFinal - childCross; break;
            }
            var rect = horizontal ? new Rect(position, offset, main, size) : new Rect(offset, position, size, main);
            child.Arrange(rect);
            position += main + Spacing;
        }
        return final;
    }

    private static int Align(HorizontalAlignment a) => a switch
    {
        HorizontalAlignment.Stretch => 0,
        HorizontalAlignment.Left => 1,
        HorizontalAlignment.Center => 2,
        _ => 3,
    };

    private static int Align(VerticalAlignment a) => a switch
    {
        VerticalAlignment.Stretch => 0,
        VerticalAlignment.Top => 1,
        VerticalAlignment.Center => 2,
        _ => 3,
    };
}

/// <summary>Children in rows, wrapping as needed, with even spacing (feature chips, cards).</summary>
public sealed class FlowPanel : Panel
{
    public double Spacing { get; set; } = 6;
    public double LineSpacing { get; set; } = 6;

    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, row = 0, widest = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(available.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > available.Width)
            {
                y += row + LineSpacing;
                x = 0;
                row = 0;
            }
            x += size.Width + Spacing;
            row = Math.Max(row, size.Height);
            widest = Math.Max(widest, x - Spacing);
        }
        return new Size(double.IsInfinity(available.Width) ? widest : Math.Min(available.Width, widest), y + row);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, row = 0;
        foreach (UIElement child in InternalChildren)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > final.Width)
            {
                y += row + LineSpacing;
                x = 0;
                row = 0;
            }
            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + Spacing;
            row = Math.Max(row, size.Height);
        }
        return final;
    }
}

/// <summary>Equal-width columns that fit at least <see cref="MinColumnWidth"/> each, like SwiftUI's adaptive grid.</summary>
public sealed class AdaptiveGrid : Panel
{
    public double MinColumnWidth { get; set; } = 250;
    public double Spacing { get; set; } = 14;

    private int Columns(double width) =>
        double.IsInfinity(width) ? Math.Max(1, InternalChildren.Count) : Math.Max(1, (int)((width + Spacing) / (MinColumnWidth + Spacing)));

    protected override Size MeasureOverride(Size available)
    {
        var columns = Columns(available.Width);
        var width = double.IsInfinity(available.Width) ? MinColumnWidth : (available.Width - Spacing * (columns - 1)) / columns;
        foreach (UIElement child in InternalChildren) child.Measure(new Size(width, double.PositiveInfinity));
        var rows = (InternalChildren.Count + columns - 1) / columns;
        double height = 0;
        for (var r = 0; r < rows; r++)
        {
            double h = 0;
            for (var c = 0; c < columns && r * columns + c < InternalChildren.Count; c++) h = Math.Max(h, InternalChildren[r * columns + c].DesiredSize.Height);
            height += h + (r > 0 ? Spacing : 0);
        }
        var usedWidth = double.IsInfinity(available.Width) ? columns * width + Spacing * (columns - 1) : available.Width;
        return new Size(usedWidth, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var columns = Columns(final.Width);
        var width = (final.Width - Spacing * (columns - 1)) / columns;
        var rows = (InternalChildren.Count + columns - 1) / columns;
        double y = 0;
        for (var r = 0; r < rows; r++)
        {
            double h = 0;
            for (var c = 0; c < columns && r * columns + c < InternalChildren.Count; c++) h = Math.Max(h, InternalChildren[r * columns + c].DesiredSize.Height);
            for (var c = 0; c < columns && r * columns + c < InternalChildren.Count; c++)
                InternalChildren[r * columns + c].Arrange(new Rect(c * (width + Spacing), y, width, h));
            y += h + Spacing;
        }
        return final;
    }
}
