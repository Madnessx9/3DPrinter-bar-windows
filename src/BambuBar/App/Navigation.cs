using BambuBar.Protocol;
using BambuBar.UI;

namespace BambuBar.App;

public enum AppSection { Fleet, Overview, Filament, Widgets, Settings, About }

public static class AppSections
{
    public static readonly AppSection[] All = [AppSection.Fleet, AppSection.Overview, AppSection.Filament, AppSection.Widgets, AppSection.Settings, AppSection.About];

    public static string Title(this AppSection section) => section switch
    {
        AppSection.Fleet => "All Printers",
        AppSection.Overview => "Printer",
        AppSection.Filament => "Filament",
        AppSection.Widgets => "Widgets",
        AppSection.Settings => "Settings",
        _ => "About",
    };

    public static Icon Icon(this AppSection section) => section switch
    {
        AppSection.Fleet => UI.Icon.Grid,
        AppSection.Overview => UI.Icon.Printer,
        AppSection.Filament => UI.Icon.Spool,
        AppSection.Widgets => UI.Icon.Widget,
        AppSection.Settings => UI.Icon.Settings,
        _ => UI.Icon.Info,
    };
}

/// <summary>Shared navigation so menus, the tray panel and notifications can open a specific page.</summary>
public sealed class Navigation
{
    public static Navigation Shared { get; } = new();

    private AppSection _section = AppSection.Overview;
    private DiscoveredPrinter? _setupCandidate;
    private bool _addingPrinter;

    public event Action? Changed;

    public AppSection Section
    {
        get => _section;
        set { if (_section != value) { _section = value; Notify(); } }
    }

    /// <summary>A discovered printer picked in the tray panel, to prefill the setup form.</summary>
    public DiscoveredPrinter? SetupCandidate
    {
        get => _setupCandidate;
        set { _setupCandidate = value; Notify(); }
    }

    /// <summary>The Printer page shows the setup form for another printer.</summary>
    public bool AddingPrinter
    {
        get => _addingPrinter;
        set { if (_addingPrinter != value) { _addingPrinter = value; Notify(); } }
    }

    private void Notify()
    {
        Changed?.Invoke();
        Live.Invalidate();
    }
}
