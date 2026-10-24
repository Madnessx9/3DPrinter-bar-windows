using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BambuBar.Models;
using BambuBar.Services;
using static BambuBar.UI.Components;
using static BambuBar.UI.Pages.PageKit;
using static BambuBar.UI.Ui;

namespace BambuBar.UI.Pages;

/// <summary>Every loaded spool, large, with material, colour, what's left and the AMS climate.</summary>
public static class FilamentPage
{
    private static PrinterStore Store => PrinterStore.Shared;

    // Only what the page shows, so temperature reports don't rebuild it.
    public static object Key() => (Store.Current?.Id, Store.Sessions.Count, Store.IsOnline, Store.IsConfigured, Store.Status?.Ams, Store.Status?.ExternalSpool,
        Store.Status?.ActiveSpoolId, Store.Status?.State.IsActive());

    public static UIElement Build()
    {
        var isMoonraker = Store.Config?.Protocol == PrinterProtocol.Moonraker;
        var rows = new List<UIElement?> { PageHeader("Filament", Subtitle(), PrinterNavArrows(showPrinterName: true)) };
        if (Store.Status is { } status && Store.IsOnline)
        {
            var printing = status.State.IsActive();
            if (status.Ams.Count == 0 && status.ExternalSpool is null)
            {
                rows.Add(isMoonraker
                    ? Empty(Icon.Spool, "No filament loaded", "Spools loaded on your printer's toolheads appear here.")
                    : Empty(Icon.Spool, "No AMS connected", "Spools loaded in an AMS or on the external holder appear here."));
            }
            foreach (var unit in status.Ams)
            {
                var grid = new AdaptiveGrid { MinColumnWidth = 150, Spacing = 12 };
                foreach (var spool in unit.Spools) grid.Children.Add(SpoolCard(spool, spool.Id == status.ActiveSpoolId && printing));
                rows.Add(Column(10, Row(8, Grow(Text(unit.Title, 16, Semibold)), Climate(unit)), grid));
            }
            if (status.ExternalSpool is { } ext)
            {
                var grid = new AdaptiveGrid { MinColumnWidth = 150, Spacing = 12 };
                grid.Children.Add(SpoolCard(ext, ext.Id == status.ActiveSpoolId && printing));
                rows.Add(Column(10, Text("External spool", 16, Semibold), grid));
            }
        }
        else
        {
            rows.Add(Store.IsConfigured
                ? Empty(Icon.Spool, "Printer offline", "Filament appears once Bambu Bar is connected.")
                : Empty(Icon.Spool, "No printer set up", "Set up your printer on the Printer page."));
        }
        return Column(20, [.. rows]);
    }

    private static string Subtitle()
    {
        var isMoonraker = Store.Config?.Protocol == PrinterProtocol.Moonraker;
        if (Store.Status is not { } s)
            return isMoonraker ? "Spools loaded on your printer's toolheads" : "Spools in your AMS and on the external holder";
        var loaded = s.AllSpools.Count(sp => sp.IsLoaded);
        var plural = loaded == 1 ? "" : "s";
        var grams = s.AllSpools.Select(sp => sp.GramsLeft).OfType<int>().ToList();
        if (grams.Count > 0 && !(s.State.IsActive() && s.ActiveSpool is not null))
            return $"{loaded} spool{plural} loaded · {Format.Grams(grams.Sum())} of filament on hand";
        if (s.ActiveSpool is { } active && s.State.IsActive()) return $"{loaded} loaded · printing with {active.Label} {active.DisplayName}";
        return $"{loaded} spool{plural} loaded";
    }

    private static FrameworkElement Climate(AmsUnit unit)
    {
        var items = new List<UIElement?>();
        if (unit.DryingMinutesLeft > 0)
            items.Add(TightLabel(Icon.Sun, $"Drying · {Format.Duration(unit.DryingMinutesLeft)} left", 12, Theme.Brush(Palette.Bed), Theme.Brush(Palette.Bed)));
        if (unit.Humidity is int h)
        {
            var c = h > 40 ? Theme.Brush(Palette.Paused) : Theme.Secondary;
            items.Add(TightLabel(Icon.Drop, $"{h}% humidity", 12, c, c));
        }
        else if (unit.HumidityLevel is int level)
        {
            var c = level >= 4 ? Theme.Brush(Palette.Paused) : Theme.Secondary;
            items.Add(TightLabel(Icon.Drop, $"Humidity {level} of 5", 12, c, c));
        }
        if (unit.Temperature is double t) items.Add(TightLabel(Icon.Thermometer, Format.Temperature(t), 12, Theme.Secondary));
        return Row(14, [.. items]);
    }

    public static FrameworkElement SpoolCard(Spool spool, bool active)
    {
        var label = new Border
        {
            Child = Text(spool.Label, 11, Bold),
            Padding = new Thickness(7, 2, 7, 2), CornerRadius = new CornerRadius(10), Background = Theme.Fill(0.08),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var badges = Column(4, label, active ? Text("In use", 10.5, Semibold, Theme.Brush(Palette.Accent)).Aligned(HorizontalAlignment.Right) : null);
        badges.VerticalAlignment = VerticalAlignment.Top;
        var swatch = new SpoolSwatch(spool, active, 58, spool.IsLoaded && spool.Weight is int w ? Format.Grams(w, approximate: false) : null);
        swatch.VerticalAlignment = VerticalAlignment.Top;
        var names = Column(2,
            Text(spool.IsLoaded ? spool.DisplayName : "Empty slot", 14, Semibold, spool.IsLoaded ? Theme.Primary : Theme.Secondary),
            Text(spool.IsLoaded ? Detail(spool) : "Load a spool to see it here", 11.5, color: Theme.Secondary));
        // Kept in place even when the printer can't measure the spool, so every card has the same proportions.
        var remaining = Remaining(spool).Fade(spool.IsLoaded && spool.Remain is not null ? 1 : 0);
        var card = Panel(Column(12, Row(8, swatch, Spacer(), badges), names, remaining), 18, 14, active ? Palette.Accent : null);
        if (active)
        {
            card.BorderBrush = Theme.Brush(Palette.Accent, 0.6);
            card.BorderThickness = new Thickness(1.2);
        }
        card.ToolTip = Help(spool);
        return card;
    }

    private static FrameworkElement Remaining(Spool spool)
    {
        var tint = Palette.Filament(spool.Color) is { } c && BambuColor.Luminance(spool.Color) <= 0.88 ? c : Theme.PrimaryColor.WithOpacity(0.6);
        var percent = Text($"{spool.Remain ?? 0}%", 10.5, Semibold, digits: true);
        percent.TextAlignment = TextAlignment.Right;
        return Column(5,
            Row(4, Text("Remaining", 10.5, color: Theme.Secondary), Spacer(),
                spool.GramsLeft is int g ? Text(Format.Grams(g), 10.5, Semibold, digits: true) : null),
            // Wide enough for "100%", so the bars line up across cards.
            Row(8, Grow(FilamentProgressBar((spool.Remain ?? 0) / 100.0, tint, 5)), percent.Sized(32)));
    }

    /// <summary>"Bambu Lab · Grass Green", with the material when the name doesn't already say it and the hex code
    /// when the colour has no Bambu name.</summary>
    private static string Detail(Spool spool)
    {
        List<string> parts = [];
        if (spool.Brand is { } brand) parts.Add(brand);
        if (!spool.DisplayName.Contains(spool.Material, StringComparison.CurrentCultureIgnoreCase)) parts.Add(spool.Material);
        parts.Add(spool.ColorName ?? "#" + spool.Color[..Math.Min(6, spool.Color.Length)]);
        return string.Join(" · ", parts);
    }

    private static string Help(Spool spool)
    {
        if (!spool.IsLoaded) return $"{spool.Label}: empty";
        var hex = "#" + spool.Color[..Math.Min(6, spool.Color.Length)];
        List<string> lines =
        [
            string.Join(" ", new[] { spool.Brand, spool.DisplayName }.OfType<string>()),
            string.Join(" · ", new[] { spool.ColorName, hex }.OfType<string>()),
        ];
        if (spool.NozzleRange is { } range) lines.Add($"Prints at {range.Low}–{range.High} °C");
        if (spool.Remain is null && Store.Config?.Protocol != PrinterProtocol.Moonraker)
            lines.Add(spool.IsExternal ? "The external holder can’t weigh the spool." : "Only Bambu Lab spools with an RFID tag report what’s left.");
        return string.Join("\n", lines);
    }
}
