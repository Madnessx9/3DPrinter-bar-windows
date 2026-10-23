using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BambuBar.Services;

public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "dev";

    /// <summary>Where the Windows port is published: its releases are what the update check offers.</summary>
    public const string Repository = "Precog29/bambu-bar-windows";
    /// <summary>The macOS original this port follows.</summary>
    public const string Upstream = "rolottr/bambu-bar";

    public static string ProjectUrl => $"https://github.com/{Repository}";
    public static string UpstreamUrl => $"https://github.com/{Upstream}";
}

/// <summary>
/// Looks for a newer published release on GitHub. It only reads the public "latest release" endpoint (no account,
/// no identifiers) and never installs anything itself: downloads happen on the release's GitHub page.
/// Lives on the main thread.
/// </summary>
public sealed class UpdateChecker
{
    public sealed record Release(string Version, string PageUrl);

    public abstract record CheckState
    {
        public sealed record Idle : CheckState;
        public sealed record Checking : CheckState;
        public sealed record UpToDate : CheckState;
        public sealed record Available(Release Release) : CheckState;
        public sealed record Failed(string Message) : CheckState;
    }

    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private const string AutomaticKey = "updateChecksAutomatically", LastCheckedKey = "updateLastChecked", SkippedKey = "updateSkippedVersion";

    private readonly SettingsStore _settings;
    private readonly HttpClient _http;
    private MainTimer? _schedule;
    private bool _firstCheckDone;

    public static UpdateChecker Shared { get; set; } = new(SettingsStore.Shared);

    public UpdateChecker(SettingsStore settings, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        LastChecked = settings.GetDate(LastCheckedKey);
        SkippedVersion = settings.GetString(SkippedKey);
    }

    public event Action? Changed;

    public string CurrentVersion { get; init; } = AppInfo.Version;
    public CheckState State { get; private set; } = new CheckState.Idle();
    public DateTime? LastChecked { get; private set; }
    /// <summary>Hidden from the unprompted offer for this run only ("Later").</summary>
    public bool OfferDismissed { get; private set; }
    public string? SkippedVersion { get; private set; }

    public bool ChecksAutomatically
    {
        get => _settings.GetBool(AutomaticKey) ?? true;
        set { _settings.Set(AutomaticKey, value); Changed?.Invoke(); }
    }

    public Release? Available => State is CheckState.Available a ? a.Release : null;

    /// <summary>The release to offer unprompted: not skipped and not put off for later.</summary>
    public Release? Offered => Available is { } r && !OfferDismissed && r.Version != SkippedVersion ? r : null;

    /// <summary>Checks shortly after launch, then hourly whether a day has passed, since the app can run for weeks in the tray.</summary>
    public void StartAutomaticChecks()
    {
        if (_schedule is not null || AppArguments.Current.IsDeveloperRun) return;
        _schedule = MainTimer.Every(TimeSpan.FromMinutes(60), () => _ = CheckIfDue());
        MainTimer.After(TimeSpan.FromSeconds(10), () =>
        {
            if (_firstCheckDone) return;
            _firstCheckDone = true;
            _ = CheckIfDue();
        });
    }

    private async Task CheckIfDue()
    {
        if (!ChecksAutomatically || State is CheckState.Checking) return;
        if (LastChecked is DateTime last && DateTime.UtcNow - last < Interval) return;
        await Check();
    }

    public async Task Check()
    {
        if (State is CheckState.Checking) return;
        State = new CheckState.Checking();
        Changed?.Invoke();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{AppInfo.Repository}/releases/latest");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd($"BambuBar-Windows/{CurrentVersion}");
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
            var release = ParseRelease(await response.Content.ReadAsByteArrayAsync());
            State = IsVersion(release.Version, CurrentVersion) ? new CheckState.Available(release) : new CheckState.UpToDate();
            LastChecked = DateTime.UtcNow;
            _settings.Set(LastCheckedKey, LastChecked.Value);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
            State = new CheckState.Failed("Couldn’t reach GitHub. Check your connection and try again.");
        }
        Changed?.Invoke();
    }

    public void DismissOffer() { OfferDismissed = true; Changed?.Invoke(); }

    public void Skip(Release release)
    {
        SkippedVersion = release.Version;
        _settings.Set(SkippedKey, release.Version);
        Changed?.Invoke();
    }

    // MARK: - Parsing

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    }

    public static Release ParseRelease(byte[] data)
    {
        var release = JsonSerializer.Deserialize<GitHubRelease>(data) ?? throw new FormatException();
        if (release.Draft || release.Prerelease || release.TagName.Length == 0 || release.HtmlUrl.Length == 0) throw new FormatException();
        var version = release.TagName.StartsWith('v') ? release.TagName[1..] : release.TagName;
        return new Release(version, release.HtmlUrl);
    }

    /// <summary>Numeric, component-wise: 1.10.0 &gt; 1.9.2, and 1.2 == 1.2.0.</summary>
    public static bool IsVersion(string candidate, string current)
    {
        static int[] Parts(string version) => version.Split('.')
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0).ToArray();
        var a = Parts(candidate);
        var b = Parts(current);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }
}
