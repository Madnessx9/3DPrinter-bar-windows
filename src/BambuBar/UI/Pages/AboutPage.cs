using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using BambuBar.Services;
using static BambuBar.UI.Components;
using static BambuBar.UI.Ui;

namespace BambuBar.UI.Pages;

/// <summary>About the app: version details for bug reports, how it works, privacy and credits.</summary>
public sealed class AboutPage : ContentControl
{
    private static UpdateChecker Updates => UpdateChecker.Shared;
    private bool _copied;

    public AboutPage()
    {
        Focusable = false;
        Content = new Live(() => (Updates.State, Updates.LastChecked, _copied), Build);
    }

    public static string SystemName
    {
        get
        {
            var v = Environment.OSVersion.Version;
            var name = v.Build >= 22000 ? "Windows 11" : "Windows 10";
            return $"{name} (build {v.Build})";
        }
    }

    public static string ArchitectureName => RuntimeInformation.OSArchitecture switch
    {
        global::System.Runtime.InteropServices.Architecture.Arm64 => "ARM64",
        global::System.Runtime.InteropServices.Architecture.X86 => "x86",
        _ => "x64",
    };

    public static string Summary => $"Bambu Bar for Windows {AppInfo.Version} · {SystemName} · {ArchitectureName}";

    private string UpdateLine => Updates.State switch
    {
        UpdateChecker.CheckState.Available a => $"{a.Release.Version} is available",
        UpdateChecker.CheckState.UpToDate => "Up to date",
        UpdateChecker.CheckState.Checking => "Checking…",
        UpdateChecker.CheckState.Failed => "Couldn’t check",
        _ => Updates.LastChecked is null ? "Not checked yet" : "Up to date",
    };

    private UIElement Build()
    {
        var icon = new System.Windows.Controls.Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Resources/AppIcon.png")), Width = 96, Height = 96,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 5, Direction = 270, Opacity = 0.25 },
        };
        var heading = new List<UIElement?>
        {
            Text("Bambu Bar", 30, Bold, display: true),
            Text($"Version {AppInfo.Version} for Windows", 12.5, color: Theme.Secondary),
            Text("Your Bambu Lab printer, one click away in the notification area.", 13, color: Theme.Tertiary),
        };
        if (Updates.Available is { } release)
        {
            var get = Push($"Get Bambu Bar {release.Version}", () => Open(release.PageUrl), accent: true);
            get.HorizontalAlignment = HorizontalAlignment.Left;
            get.Margin = new Thickness(0, 6, 0, 0);
            get.ToolTip = $"Opens the {release.Version} release on GitHub";
            heading.Add(get);
        }
        UIElement updateAction = Updates.Available is { } r
            ? Push("Download", () => Open(r.PageUrl), small: true)
            : Updates.State is UpdateChecker.CheckState.Checking ? new Spinner(12) : Push("Check Now", () => _ = Updates.Check(), small: true);

        return Column(22,
            Row(20, icon, Grow(Column(4, [.. heading]))).WithMargin(0, 0, 0, 4),
            Section("This copy",
                DetailRow("Version", AppInfo.Version),
                DetailRow("System", $"{SystemName} · {ArchitectureName}"),
                DetailRow("License", "MIT, free and open source"),
                Row(8, Text("Updates", 12, color: Theme.Secondary), Spacer(),
                    Text(UpdateLine, 12, color: Updates.Available is null ? Theme.Secondary : Theme.Brush(Palette.Accent)), updateAction),
                Row(8, Text("For bug reports", 12, color: Theme.Secondary), Spacer(), Push(_copied ? "Copied" : "Copy Version Info", Copy, small: true))),
            Section("How it works",
                Paragraph("Bambu Bar connects straight to your printer on the local network, the same way Bambu Studio does in LAN mode: an encrypted MQTT connection on port 8883, signed in with the printer's access code. The printer's certificate is checked against Bambu Lab's own certificate authority."),
                Paragraph("The printer streams its status about once a second. Bambu Bar shows it in the notification area, keeps a short history of your prints, and draws its desktop widgets from the same live data.")),
            Section("Privacy",
                Paragraph("No account, no cloud, no analytics. Bambu Bar talks to your printer on your network, and the access code is stored in Windows Credential Manager."),
                Paragraph("Its only other request is the optional daily update check, which reads this project's latest release on GitHub and sends nothing about you or your printers. Turn it off in Settings → Updates.")),
            Section("Links",
                Link("Project on GitHub", () => Open(AppInfo.ProjectUrl), Icon.Code, tip: AppInfo.ProjectUrl),
                Link("Release Notes", () => Open($"{AppInfo.ProjectUrl}/releases/tag/v{AppInfo.Version}"), Icon.Document),
                Link("Report an Issue", () => Open($"{AppInfo.ProjectUrl}/issues/new"), Icon.Feedback),
                Link("The original macOS app", () => Open(AppInfo.UpstreamUrl), Icon.Window, tip: AppInfo.UpstreamUrl)),
            Section("Credits",
                Paragraph("Bambu Bar was created for macOS by rolottr; this is its Windows port, built from the same design and sources."),
                Paragraph("Protocol details and the English HMS error texts come from the open-source ha-bambulab project (MIT), with thanks to Bambuddy and bambu-cli for documenting the newer printers. What each model can do (chamber sensor, fans, heated chamber, nozzles, build volume) follows the printer profiles Bambu Lab publishes with Bambu Studio."),
                Paragraph("The printer models were built for this project in Blender. They are visual approximations, not manufacturer CAD."),
                Paragraph("Bambu Lab, Bambu Studio and P2S are trademarks of Bambu Lab. Bambu Bar is an independent project, not affiliated with or endorsed by Bambu Lab.")));
    }

    private static FrameworkElement Section(string title, params UIElement[] content) =>
        Column(8, SectionLabel(title), Panel(Column(10, content), 16, 16));

    private static TextBlock Paragraph(string text) => Text(text, 12.5, color: Theme.Secondary, wrap: true);

    private void Copy()
    {
        try { Clipboard.SetText(Summary); }
        catch (COMException) { return; }  // the clipboard is busy in another app
        _copied = true;
        Live.Invalidate();
        MainTimer.After(TimeSpan.FromSeconds(2), () => { _copied = false; Live.Invalidate(); });
    }
}
