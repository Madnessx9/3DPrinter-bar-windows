using System.Windows;
using System.Windows.Controls;
using BambuBar.App;
using BambuBar.Models;
using BambuBar.Services;
using static BambuBar.UI.PanelViews;
using static BambuBar.UI.Pages.PageKit;
using static BambuBar.UI.Ui;

namespace BambuBar.UI.Pages;

/// <summary>Every printer at a glance, for people with more than a couple.</summary>
public static class FleetPage
{
    private static PrinterStore Store => PrinterStore.Shared;

    public static object Key() => string.Join("|", Store.Sessions.Select(s => (s.Id, s.Config.Name, s.Status, s.Phase).GetHashCode()));

    public static UIElement Build()
    {
        var grid = new AdaptiveGrid { MinColumnWidth = 250, Spacing = 14 };
        foreach (var session in Store.FleetSessions)
        {
            var id = session.Id;
            grid.Children.Add(FleetCard(session, () =>
            {
                Store.Select(id);
                Navigation.Shared.Section = AppSection.Overview;
            }));
        }
        return Column(18,
            PageHeader("All Printers", $"{Store.Sessions.Count} printers · {Store.FleetSummary}", Push("Add Printer…", Shell.AddPrinter)),
            grid);
    }

    /// <summary>One printer in the fleet grid: its render, state and job.</summary>
    public static FrameworkElement FleetCard(PrinterSession session, Action open)
    {
        var tint = session.Status is { } st ? Palette.ColorFor(st) : Palette.Failed;
        var content = Row(12,
            new PrinterArtView(PrinterArt.For(session.Status, session.Config.ModelName, dimmed: !session.IsOnline, animated: false), 64, 76),
            Grow(Column(5,
                Row(6, Grow(Text(session.Config.Name, 13.5, Semibold)), StateChip(session)),
                Text(session.FleetDetail, 11.5, color: Theme.Secondary),
                FleetProgress(session))));
        var panel = Panel(Pad(content, 12), 16, 0, session.NeedsAttention ? tint : null);
        if (session.NeedsAttention)
        {
            panel.BorderBrush = Theme.Brush(tint, 0.55);
            panel.BorderThickness = new Thickness(1);
        }
        var idle = panel.Background;
        return Button(panel, open, t =>
        {
            panel.Background = t.IsMouseOver && !session.NeedsAttention ? Theme.Fill(0.08) : idle;
        }, $"{session.Config.Name}: {session.FleetDetail}");
    }
}
