using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace BambuBar.UI;

/// <summary>
/// A piece of UI rebuilt from state, the way SwiftUI re-evaluates a view's body. It rebuilds only when its key (a
/// value with value equality: records, tuples, strings) changes, and only while it's on screen, so the panel and window
/// stay cheap however often the printer reports. Everything that can change calls <see cref="Invalidate"/>; the
/// rebuilds run together shortly after, on the main thread.
/// </summary>
public sealed class Live : ContentControl
{
    private static readonly List<WeakReference<Live>> All = [];
    private static bool _scheduled;

    private readonly Func<object?> _key;
    private readonly Func<UIElement?> _build;
    private readonly bool? _theme;
    private object? _last;
    private int _themeVersion;
    private bool _stale = true;

    public Live(Func<object?> key, Func<UIElement?> build)
    {
        _key = key;
        _build = build;
        // Views built inside a theme override (widgets) keep it when they rebuild.
        _theme = Theme.CurrentOverride;
        Focusable = false;
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        lock (All) All.Add(new WeakReference<Live>(this));
        Update();
        IsVisibleChanged += (_, _) => { if (IsVisible && _stale) Update(); };
    }

    /// <summary>Rebuilds with a key that never matches: for content that shows the time ("3s ago").</summary>
    public static Live Always(Func<UIElement?> build) => new(() => new object(), build);

    /// <summary>Rebuilds if the key changed (or the theme did).</summary>
    public void Update()
    {
        var key = _key();
        if (!_stale && _themeVersion == Theme.Version && Equals(key, _last)) return;
        _last = key;
        _themeVersion = Theme.Version;
        _stale = false;
        // A kept text field that had focus loses it when it's moved into the rebuilt content; give it back.
        var focused = Keyboard.FocusedElement as UIElement;
        if (focused is not null && !IsAncestorOf(focused)) focused = null;
        using (Theme.Override(_theme))
        {
            var content = _build();
            if (content is not null) Reuse.Detach(content);
            Content = content;
        }
        if (focused is not null && !focused.IsKeyboardFocused)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (IsAncestorOf(focused)) focused.Focus(); });
    }

    /// <summary>Something changed: rebuild every visible view whose key moved, once the current work is done.</summary>
    public static void Invalidate()
    {
        if (_scheduled) return;
        _scheduled = true;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, Flush);
    }

    private static void Flush()
    {
        _scheduled = false;
        List<Live> live = [];
        lock (All)
        {
            All.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in All) if (w.TryGetTarget(out var l)) live.Add(l);
        }
        foreach (var view in live)
        {
            // Hidden views (a closed panel, another page) catch up when they're shown again.
            if (view.IsVisible) view.Update();
            else view._stale = true;
        }
    }
}

/// <summary>Controls kept across rebuilds (text fields, the 3D view), so typing, focus and camera survive.</summary>
public static class Reuse
{
    /// <summary>Takes an element out of whatever it was placed in last, so it can be placed again.</summary>
    public static void Detach(UIElement element)
    {
        var parent = (element as FrameworkElement)?.Parent ?? System.Windows.Media.VisualTreeHelper.GetParent(element);
        switch (parent)
        {
            case Panel panel: panel.Children.Remove(element); break;
            case ContentControl control when ReferenceEquals(control.Content, element): control.Content = null; break;
            case Decorator decorator when ReferenceEquals(decorator.Child, element): decorator.Child = null; break;
            case ContentPresenter presenter when ReferenceEquals(presenter.Content, element): presenter.Content = null; break;
        }
    }

    /// <summary>The element, detached and ready to place.</summary>
    public static T Place<T>(T element) where T : UIElement
    {
        Detach(element);
        return element;
    }
}
