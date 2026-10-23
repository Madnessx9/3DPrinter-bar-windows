using BambuBar.Protocol;

namespace BambuBar.App;

/// <summary>What views ask of the app: open the window on a page, hide the tray panel, quit. Implemented by
/// <see cref="AppHost"/>; snapshots run without one.</summary>
public interface IShell
{
    void ShowMainWindow(AppSection? section = null, DiscoveredPrinter? setup = null);
    void HideFlyout();
    void Quit();
    void AddWidget(UI.Widgets.WidgetSize size, string? serial);
}

public static class Shell
{
    public static IShell? Current { get; set; }

    public static void ShowMainWindow(AppSection? section = null, DiscoveredPrinter? setup = null)
    {
        if (section is { } s) Navigation.Shared.Section = s;
        if (setup is not null) Navigation.Shared.SetupCandidate = setup;
        Current?.ShowMainWindow(section, setup);
    }

    /// <summary>Opens the setup form to add another printer.</summary>
    public static void AddPrinter()
    {
        Navigation.Shared.AddingPrinter = true;
        ShowMainWindow(AppSection.Overview);
    }

    public static void HideFlyout() => Current?.HideFlyout();
    public static void Quit() => Current?.Quit();
    public static void AddWidget(UI.Widgets.WidgetSize size, string? serial = null) => Current?.AddWidget(size, serial);
}
