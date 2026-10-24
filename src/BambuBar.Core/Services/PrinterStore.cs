using BambuBar.Models;
using BambuBar.Protocol;

namespace BambuBar.Services;

/// <summary>What the tray icon's tooltip and badge show while printing.</summary>
public enum MenuBarStyle { Icon, Percent, Remaining, Both }

public static class MenuBarStyleExtensions
{
    public static readonly MenuBarStyle[] All = [MenuBarStyle.Icon, MenuBarStyle.Percent, MenuBarStyle.Remaining, MenuBarStyle.Both];

    public static string Title(this MenuBarStyle style) => style switch
    {
        MenuBarStyle.Icon => "Icon only",
        MenuBarStyle.Percent => "Progress",
        MenuBarStyle.Remaining => "Time left",
        _ => "Progress and time left",
    };

    public static string Key(this MenuBarStyle style) => style switch
    {
        MenuBarStyle.Icon => "icon",
        MenuBarStyle.Percent => "percent",
        MenuBarStyle.Remaining => "remaining",
        _ => "both",
    };

    public static MenuBarStyle Parse(string? key) => All.FirstOrDefault(s => s.Key() == key, MenuBarStyle.Both);
}

/// <summary>
/// The app's single source of truth: the printers, which one is in view, job history and settings.
///
/// The window and the tray panel show the <i>selected</i> printer; the members below without a session
/// argument (<see cref="Status"/>, <see cref="Pause"/>…) act on it. The tray icon shows <see cref="MenuBarSession"/>, which follows
/// the Settings choice ("Automatic" picks whichever printer is busy). Lives on the main thread.
/// </summary>
public sealed class PrinterStore
{
    public static PrinterStore Shared { get; set; } = null!;

    public sealed record ToastMessage(string Message, bool IsError)
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private readonly List<PrinterSession> _sessions = [];
    public IReadOnlyList<PrinterSession> Sessions => _sessions;
    public string? SelectedSerial { get; private set; }
    public List<JobRecord> History { get; private set; } = [];
    public List<DiscoveredPrinter> Discovered { get; } = [];
    /// <summary>Printers whose access code a slicer already knows (this PC's Bambu Studio or OrcaSlicer, or a settings
    /// file imported from another computer this session).</summary>
    public IReadOnlyList<SlicerPrinter> SlicerPrinters { get; private set; } = [];
    private readonly List<SlicerPrinter> _importedPrinters = [];
    /// <summary>A short-lived message after a command ("The printer refused…").</summary>
    public ToastMessage? Toast { get; private set; }
    public bool IsDemo { get; private set; }

    /// <summary>Anything shown anywhere changed. Raised on the main thread, possibly many times in a row.</summary>
    public event Action? Changed;

    /// <summary>Asks the person to confirm stopping a print: (title, message) → stop?</summary>
    public Func<string, string, bool>? ConfirmStopPrompt { get; set; }

    private readonly SettingsStore _settings;
    private readonly ICredentialStore _credentials;
    private readonly INotifier _notifier;
    private readonly HistoryStore _historyStore;
    private readonly PrinterDiscovery _discovery;
    private MainTimer? _toastTimer;
    private bool _insecureTls;

    private static class Keys
    {
        public const string Printers = "printers", Selected = "selectedPrinter", MenuBarPrinter = "menuBarPrinter",
            MenuBarStyle = "menuBarStyle", NotifyFinished = "notifyFinished", NotifyFailed = "notifyFailed",
            NotifyPaused = "notifyPaused", NotifyStarted = "notifyStarted", ConfirmStop = "confirmStop",
            PanelShowsAll = "panelShowsAllPrinters", FleetActiveOnly = "fleetActiveOnly";
    }

    public PrinterStore(SettingsStore settings, ICredentialStore credentials, INotifier notifier, HistoryStore history,
        PrinterDiscovery? discovery = null)
    {
        _settings = settings;
        _credentials = credentials;
        _notifier = notifier;
        _historyStore = history;
        _discovery = discovery ?? new PrinterDiscovery(AppArguments.Current.Has("--emulator") ? [2121] : []);
        _insecureTls = AppArguments.Current.InsecureTls;
        SelectedSerial = settings.GetString(Keys.Selected);
    }

    // MARK: - Preferences

    /// <summary><c>null</c> = automatic.</summary>
    public string? MenuBarSerial
    {
        get => _settings.GetString(Keys.MenuBarPrinter);
        set { _settings.Set(Keys.MenuBarPrinter, value); NotifyChanged(); }
    }

    public MenuBarStyle MenuBarStyle
    {
        get => MenuBarStyleExtensions.Parse(_settings.GetString(Keys.MenuBarStyle));
        set { _settings.Set(Keys.MenuBarStyle, value.Key()); NotifyChanged(); }
    }

    public bool NotifyFinished { get => _settings.GetBool(Keys.NotifyFinished) ?? true; set => SetNotify(Keys.NotifyFinished, value); }
    public bool NotifyFailed { get => _settings.GetBool(Keys.NotifyFailed) ?? true; set => SetNotify(Keys.NotifyFailed, value); }
    public bool NotifyPaused { get => _settings.GetBool(Keys.NotifyPaused) ?? true; set => SetNotify(Keys.NotifyPaused, value); }
    public bool NotifyStarted { get => _settings.GetBool(Keys.NotifyStarted) ?? false; set => SetNotify(Keys.NotifyStarted, value); }

    public bool ConfirmStop
    {
        get => _settings.GetBool(Keys.ConfirmStop) ?? true;
        set { _settings.Set(Keys.ConfirmStop, value); NotifyChanged(); }
    }

    /// <summary>The tray panel lists every printer instead of showing one (defaults to true when multiple printers are configured).</summary>
    public bool PanelShowsAll
    {
        get => _settings.GetBool(Keys.PanelShowsAll) ?? true;
        set { _settings.Set(Keys.PanelShowsAll, value); NotifyChanged(); }
    }

    /// <summary>Filter fleet views (tray panel and All Printers page) to only show active/printing printers.</summary>
    public bool FleetActiveOnly
    {
        get => _settings.GetBool(Keys.FleetActiveOnly) ?? false;
        set { _settings.Set(Keys.FleetActiveOnly, value); NotifyChanged(); }
    }

    private void SetNotify(string key, bool value)
    {
        _settings.Set(key, value);
        RequestNotificationsIfNeeded();
        NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke();

    // MARK: - Developer runs

    /// <summary><c>--emulator &lt;BambuStudio.conf&gt; [--add-all]</c>: the mock fleet from scripts/fleet. Its printers come from the
    /// settings file it writes, as if Bambu Studio had saved them; nothing is saved, not even the access codes.</summary>
    public static string? EmulatorSettings => AppArguments.Current.Value("--emulator") is { } path
        ? Environment.ExpandEnvironmentVariables(path.StartsWith('~') ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\')) : path)
        : null;

    public static (string Host, string Serial, string Code)? DeveloperConnection =>
        AppArguments.Current.Values("--connect", 3) is { } v ? (v[0], v[1], v[2]) : null;

    // MARK: - Lifecycle

    public void Start()
    {
        _discovery.Found += OnFound;

        if (DemoDriver.RequestedScenario is { } scenario)
        {
            StartDemo(scenario);
            return;
        }
        if (DeveloperConnection is { } dev)
        {
            // `--connect <host> <serial> <code>`: a throwaway session (the local emulator, a borrowed printer).
            // Nothing is saved: not the printer, not the access code, not the history.
            IsDemo = true;
            var session = Add(new PrinterConfig { Host = dev.Host, Serial = dev.Serial, Name = "P2S Emulator", ModelCode = "N7" });
            SelectedSerial = session.Id;
            session.Start(dev.Code, insecure: _insecureTls);
            NotifyChanged();
            return;
        }
        if (EmulatorSettings is { } settings)
        {
            IsDemo = true;
            _insecureTls = true;
            _discovery.Start();
            ImportSlicerSettings(settings);
            if (AppArguments.Current.Has("--add-all")) _ = AddAllWhenAnnounced();
            return;
        }

        foreach (var config in LoadConfigs())
        {
            var session = Add(config);
            if (config.Protocol == PrinterProtocol.Moonraker)
                session.Start(_credentials.AccessCode(config.Serial) ?? "", insecure: _insecureTls);
            else if (_credentials.AccessCode(config.Serial) is { } code)
                session.Start(code, insecure: _insecureTls);
        }
        History = _historyStore.Load();
        // History from single-printer versions belongs to the printer that was set up then.
        if (_sessions.FirstOrDefault() is { } first && History.Any(j => j.PrinterSerial is null))
        {
            foreach (var job in History.Where(j => j.PrinterSerial is null)) job.PrinterSerial = first.Id;
            _historyStore.Save(History);
        }
        if (SelectedSerial is null || Session(SelectedSerial) is null) SelectedSerial = _sessions.FirstOrDefault()?.Id;
        _discovery.Start();
        ReloadSlicerPrinters();
        NotifyChanged();
    }

    /// <summary>Waits a little for the fleet's announcements, which carry the printers' names and models.</summary>
    private async Task AddAllWhenAnnounced()
    {
        for (var i = 0; i < 16; i++)
        {
            if (SlicerPrinters.Count > 0 && SlicerPrinters.All(p => Discovered.Any(d => d.Serial == p.Serial))) break;
            await Task.Delay(500);
        }
        MainThread.Post(() => AddAll(SlicerPrinters));
    }

    public void Stop()
    {
        foreach (var session in _sessions) session.Stop();
        _discovery.Stop();
    }

    private List<PrinterConfig> LoadConfigs() => _settings.Get<List<PrinterConfig>>(Keys.Printers) ?? [];

    private void SaveConfigs()
    {
        if (IsDemo) return;
        _settings.SetObject(Keys.Printers, _sessions.Select(s => s.Config).ToList());
    }

    internal PrinterSession Add(PrinterConfig config)
    {
        var session = new PrinterSession(config, _credentials);
        session.StatusChanged += (s, old, fresh, canceled) => TrackJob(s, old, fresh, canceled);
        session.Acknowledged += (s, _, ok, reason) => OnAcknowledged(s, ok, reason);
        session.Changed += NotifyChanged;
        _sessions.Add(session);
        return session;
    }

    // MARK: - Printers

    public PrinterSession? Session(string serial) => _sessions.FirstOrDefault(s => s.Id == serial);

    /// <summary>The printer the window and panel show.</summary>
    public PrinterSession? Current => (SelectedSerial is { } serial ? Session(serial) : null) ?? _sessions.FirstOrDefault();

    public void Select(string serial)
    {
        if (Session(serial) is null || SelectedSerial == serial) return;
        SelectedSerial = serial;
        if (!IsDemo) _settings.Set(Keys.Selected, serial);
        NotifyChanged();
    }

    /// <summary>The printer the tray icon follows: the one chosen in Settings, or automatically the busiest.</summary>
    public PrinterSession? MenuBarSession
    {
        get
        {
            if (MenuBarSerial is { } chosenSerial && Session(chosenSerial) is { } chosen) return chosen;
            static int Rank(PrinterSession s)
            {
                if (s.Status is not { } status || !s.IsOnline) return 0;
                if (status.MostSevereIssue is { } issue && issue.Severity <= IssueSeverity.Serious && status.State.IsActive()) return 4;
                return status.State switch
                {
                    JobState.Paused => 4,
                    JobState.Printing or JobState.Preparing => 3,
                    JobState.Failed or JobState.Finished => 1,
                    _ => 0,
                };
            }
            // Ties go to the last-listed printer, like Swift's max(by:).
            PrinterSession? best = null;
            foreach (var s in _sessions) if (best is null || Rank(s) >= Rank(best)) best = s;
            if (best is not null && Rank(best) >= 3) return best;
            return Current;
        }
    }

    public bool HasMultiplePrinters => _sessions.Count > 1;

    /// <summary>Saves a printer (new, or a new address/code for a known one) and connects to it.</summary>
    public void Connect(PrinterConfig config, string accessCode, IEnumerable<string>? alternatives = null)
    {
        var code = accessCode.Trim();
        if (!IsDemo) _credentials.SetAccessCode(code, config.Serial);
        PrinterSession session;
        if (Session(config.Serial) is { } existing)
        {
            existing.Config = config;
            session = existing;
        }
        else
        {
            session = Add(config);
        }
        SaveConfigs();
        Select(config.Serial);
        session.Start(code, alternatives, _insecureTls);
        NotifyChanged();
    }

    /// <summary>Slicer printers that can be added right away: not set up yet, with an address from the network or the
    /// slicer's settings.</summary>
    public List<SlicerPrinter> ReadySlicerPrinters =>
        SlicerPrinters.Where(p => Session(p.Serial) is null && HostFor(p) is not null).ToList();

    private string? HostFor(SlicerPrinter printer) => Discovered.FirstOrDefault(d => d.Serial == printer.Serial)?.Host ?? printer.Host;

    /// <summary>Adds every printer a slicer knows in one go, named and typed from their network announcements when
    /// they've been seen, else after their model and serial ("P2S 495").</summary>
    public void AddAll(IEnumerable<SlicerPrinter> printers)
    {
        var added = new List<PrinterSession>();
        foreach (var printer in printers.OrderBy(NameFor, NaturalStringComparer.Instance))
        {
            if (Session(printer.Serial) is not null) continue;
            if (HostFor(printer) is not { } host || printer.AccessCodes.FirstOrDefault() is not { } code) continue;
            var seen = Discovered.FirstOrDefault(d => d.Serial == printer.Serial);
            var config = new PrinterConfig
            {
                Host = host, Serial = printer.Serial.ToUpperInvariant(), Name = NameFor(printer), ModelCode = seen?.ModelCode ?? "",
            };
            if (!IsDemo) _credentials.SetAccessCode(code, config.Serial);
            var session = Add(config);
            session.Start(code, printer.AccessCodes.Skip(1), _insecureTls);
            added.Add(session);
        }
        if (added.Count == 0) return;
        SaveConfigs();
        if (SelectedSerial is null || Session(SelectedSerial) is null) Select(added[0].Id);
        NotifyChanged();
    }

    private string NameFor(SlicerPrinter printer)
    {
        var seen = Discovered.FirstOrDefault(d => d.Serial == printer.Serial);
        if (seen is { Name: { Length: > 0 } name } && name != printer.Serial) return name;
        var model = PrinterModel.Find(seen?.ModelCode ?? "", printer.Serial);
        var suffix = printer.Serial.Length > 3 ? printer.Serial[^3..] : printer.Serial;
        return $"{model?.Name ?? "Printer"} {suffix}";
    }

    /// <summary>The name the printer announces on the network (set in Bambu Handy or Bambu Studio), when it has been seen.</summary>
    public string? AnnouncedName(string serial) =>
        Discovered.FirstOrDefault(d => d.Serial == serial)?.Name is { Length: > 0 } name && name != serial ? name : null;

    public void Rename(string serial, string name)
    {
        if (Session(serial) is not { } session) return;
        var trimmed = name.Trim();
        session.Config = session.Config with { Name = trimmed.Length == 0 ? serial : trimmed };
        SaveConfigs();
        NotifyChanged();
    }

    public void Forget(string serial)
    {
        if (Session(serial) is not { } session) return;
        session.Stop();
        session.Dispose();
        _credentials.RemoveAccessCode(serial);
        _sessions.Remove(session);
        if (SelectedSerial == serial) SelectedSerial = _sessions.FirstOrDefault()?.Id;
        if (MenuBarSerial == serial) _settings.Remove(Keys.MenuBarPrinter);
        SaveConfigs();
        NotifyChanged();
    }

    public void Refresh()
    {
        _discovery.Search();
        Current?.Refresh();
    }

    public void SearchForPrinters()
    {
        _discovery.Search();
        ReloadSlicerPrinters();
    }

    private void OnFound(DiscoveredPrinter printer)
    {
        var index = Discovered.FindIndex(d => d.Serial == printer.Serial);
        if (index >= 0) Discovered[index] = printer;
        else Discovered.Add(printer);
        // Follow a printer when DHCP hands it a new address.
        if (Session(printer.Serial) is { } session && session.Config.Host != printer.Host && !IsDemo)
        {
            session.Config = session.Config with { Host = printer.Host };
            SaveConfigs();
            session.Restart();
        }
        NotifyChanged();
    }

    // MARK: - Access codes from slicers

    /// <summary>Rescans this PC's slicer settings.</summary>
    public void ReloadSlicerPrinters()
    {
        _ = Task.Run(() =>
        {
            var scanned = SlicerImport.Scan();
            MainThread.Post(() =>
            {
                var all = new List<SlicerPrinter>(_importedPrinters);
                foreach (var printer in scanned)
                {
                    if (!all.Any(p => p.Serial == printer.Serial)) all.Add(printer);
                }
                if (!all.SequenceEqual(SlicerPrinters))
                {
                    SlicerPrinters = all;
                    NotifyChanged();
                }
            });
        });
    }

    /// <summary>Reads a <c>BambuStudio.conf</c> / <c>OrcaSlicer.conf</c> the person picked or dropped (say, copied from the computer
    /// that runs the slicer). Returns how many printers it knew; they're kept for this session only.</summary>
    public int ImportSlicerSettings(string path)
    {
        var found = SlicerImport.ReadPicked(path);
        foreach (var printer in found)
        {
            _importedPrinters.RemoveAll(p => p.Serial == printer.Serial);
            _importedPrinters.Insert(0, printer);
        }
        var all = new List<SlicerPrinter>(_importedPrinters);
        foreach (var printer in SlicerPrinters)
        {
            if (!all.Any(p => p.Serial == printer.Serial)) all.Add(printer);
        }
        SlicerPrinters = all;
        NotifyChanged();
        return found.Count;
    }

    public SlicerPrinter? SlicerPrinter(string serial) =>
        SlicerPrinters.FirstOrDefault(p => string.Equals(p.Serial, serial, StringComparison.OrdinalIgnoreCase));

    /// <summary>A slicer code for the current printer that differs from the one that was just rejected.</summary>
    public SlicerPrinter? ReplacementCode
    {
        get
        {
            if (Current is not { } current || SlicerPrinter(current.Id) is not { } printer) return null;
            var code = current.AccessCode;
            return printer.AccessCodes.Any(c => c != code) ? printer : null;
        }
    }

    public void UseReplacementCode()
    {
        if (Current is not { } current || ReplacementCode is not { } printer) return;
        var code = current.AccessCode;
        var codes = printer.AccessCodes.Where(c => c != code).ToList();
        Connect(current.Config, codes[0], codes.Skip(1));
    }

    // MARK: - The current printer

    public bool IsConfigured => _sessions.Count > 0;
    public PrinterConfig? Config => Current?.Config;
    public ConnectionPhase Phase => Current?.Phase ?? ConnectionPhase.IdlePhase;
    public PrinterStatus? Status => Current?.Status;
    public bool IsOnline => Current?.IsOnline ?? false;
    public string PrinterName => Current?.Config.Name ?? "Printer";
    public bool ChamberLightOn => Current?.ChamberLightOn ?? false;
    public SpeedLevel Speed => Current?.Speed ?? SpeedLevel.Standard;
    public string? AccessCode => Current?.AccessCode;
    public string LinkSummary => Current?.LinkSummary ?? "No printer set up";
    public string? LinkDetail => Current?.LinkDetail;

    /// <summary>The current printer's most recent print (for "Last: …" and "finished 3h ago").</summary>
    public JobRecord? LastJob => HistoryFor(Current?.Id).FirstOrDefault();

    public List<JobRecord> HistoryFor(string? serial) => serial is null ? [] : History.Where(j => j.PrinterSerial == serial).ToList();

    // MARK: - Commands (current printer)

    public void Pause()
    {
        if (Current is not { } current || !EnsureControllable(current)) return;
        current.Pause();
        ShowToast("Pausing…");
    }

    public void Resume()
    {
        if (Current is not { } current || !EnsureControllable(current)) return;
        current.Resume();
        ShowToast("Resuming…");
    }

    public void StopPrint()
    {
        if (Current is not { } current || !EnsureControllable(current)) return;
        if (ConfirmStop && ConfirmStopPrompt is { } confirm)
        {
            var job = current.Status?.JobName ?? "";
            var title = $"Stop “{(job.Length == 0 ? "this print" : job)}”?";
            var message = (HasMultiplePrinters ? $"On {current.Config.Name}. " : "") + "The print can't be resumed after it's stopped.";
            if (!confirm(title, message)) return;
        }
        current.StopPrint();
        ShowToast("Stopping…");
    }

    public void ToggleLight() => Current?.ToggleLight();

    public void SetSpeed(SpeedLevel level)
    {
        if (Current is not { } current || !EnsureControllable(current)) return;
        current.SetSpeed(level);
    }

    /// <summary>Print commands need the printer in Developer Mode; say so instead of sending something it will refuse.</summary>
    private bool EnsureControllable(PrinterSession session)
    {
        if (session.Status?.ControlsLocked == true)
        {
            ShowToast("Turn on LAN Only and Developer Mode on the printer to control prints from Bambu Bar.", error: true);
            return false;
        }
        return true;
    }

    private void OnAcknowledged(PrinterSession session, bool ok, string? reason)
    {
        if (ok) return;
        var detail = (reason ?? "").Contains("verify", StringComparison.OrdinalIgnoreCase)
            ? "The printer only accepts commands in Developer Mode."
            : reason is not null ? $"The printer refused: {reason}" : "The printer refused the command.";
        ShowToast(HasMultiplePrinters ? $"{session.Config.Name}: {detail}" : detail, error: true);
    }

    public void ShowToast(string message, bool error = false)
    {
        Toast = new ToastMessage(message, error);
        _toastTimer?.Dispose();
        var shown = Toast;
        _toastTimer = MainTimer.After(TimeSpan.FromSeconds(error ? 6 : 3), () =>
        {
            if (Toast == shown) { Toast = null; NotifyChanged(); }
        });
        NotifyChanged();
    }

    // MARK: - Recent prints and notifications

    private void TrackJob(PrinterSession session, PrinterStatus? old, PrinterStatus fresh, bool canceled)
    {
        var serial = session.Id;
        // Simulated printers never post real notifications.
        var notify = session.HasBaseline && !IsDemo;
        var name = fresh.JobName.Length == 0 ? "Untitled print" : fresh.JobName;
        var spool = fresh.ActiveSpool;
        // Notification titles name the printer once there's more than one.
        var prefix = HasMultiplePrinters ? $"{session.Config.Name}: " : "";
        var running = History.FindIndex(j => j.PrinterSerial == serial && j.Outcome == JobOutcome.Running);

        // Start of a job, or the first report of one already running.
        if (fresh.State.IsActive() && (old is null || !old.State.IsActive()))
        {
            var current = running >= 0 ? History[running] : null;
            var sameJob = current?.Name == name && (current?.TaskId is null || fresh.TaskId is null || current.TaskId == fresh.TaskId);
            if (!sameJob)
            {
                // Anything still marked running on this printer ended while the app wasn't looking.
                foreach (var job in History.Where(j => j.PrinterSerial == serial && j.Outcome == JobOutcome.Running)) job.Outcome = JobOutcome.Ended;
                var elapsed = EstimateElapsed(fresh);
                History.Insert(0, new JobRecord
                {
                    Name = name, StartedAt = DateTime.UtcNow - elapsed, EndedAt = null, Outcome = JobOutcome.Running,
                    TotalLayers = fresh.TotalLayers, FilamentColor = spool?.Color, FilamentMaterial = spool?.Material,
                    TaskId = fresh.TaskId, PrinterSerial = serial,
                });
                _historyStore.Save(History);
            }
            if (notify && old is not null && NotifyStarted)
                _notifier.Post(prefix + "Print started", name, $"start.{serial}");
        }
        var index = History.FindIndex(j => j.PrinterSerial == serial && j.Outcome == JobOutcome.Running);
        if (index >= 0)
        {
            var before = History[index];
            var job = before with { };
            // Keep details fresh while it runs (the name arrives late on printer-started jobs).
            job.Name = name;
            if (fresh.TotalLayers > 0) job.TotalLayers = fresh.TotalLayers;
            if (spool is not null) { job.FilamentColor = spool.Color; job.FilamentMaterial = spool.Material; }
            if (!fresh.State.IsActive())
            {
                job.EndedAt = DateTime.UtcNow;
                job.Outcome = fresh.State switch
                {
                    JobState.Finished => JobOutcome.Finished,
                    JobState.Failed => canceled ? JobOutcome.Canceled : JobOutcome.Failed,
                    // Straight back to idle without FINISH: stopped from the printer, or the report was missed.
                    _ => canceled ? JobOutcome.Canceled : JobOutcome.Ended,
                };
                if (notify) NotifyEnd(job, fresh, prefix, serial);
            }
            if (job != before)
            {
                History[index] = job;
                _historyStore.Save(History);
            }
        }
        if (notify && old is not null && old.State is JobState.Printing or JobState.Preparing && fresh.State == JobState.Paused && NotifyPaused)
            _notifier.Post(prefix + "Print paused", $"{name}: {fresh.DetailText}", $"pause.{serial}");
        if (notify && old is not null)
        {
            var freshIssues = fresh.Issues.Where(i => i.Severity <= IssueSeverity.Serious && !old.Issues.Any(o => o.Id == i.Id)).ToList();
            if (freshIssues.FirstOrDefault() is { } issue && (NotifyFailed || NotifyPaused))
                _notifier.Post($"{session.Config.Name}: {issue.Severity.Title()}", issue.Message, $"{issue.Id}.{serial}");
        }
    }

    private void NotifyEnd(JobRecord job, PrinterStatus status, string prefix, string serial)
    {
        var duration = job.Duration is TimeSpan d ? $" in {Format.Duration(d)}" : "";
        switch (job.Outcome)
        {
            case JobOutcome.Finished when NotifyFinished:
                _notifier.Post(prefix + "Print finished", $"{job.Name} finished{duration}. It's ready to take off the plate.", $"end.{serial}");
                break;
            case JobOutcome.Failed when NotifyFailed:
                _notifier.Post(prefix + "Print failed", $"{job.Name}: {status.Issues.FirstOrDefault()?.Message ?? "the printer stopped the job."}", $"end.{serial}");
                break;
            case JobOutcome.Canceled when NotifyFailed:
                _notifier.Post(prefix + "Print canceled", job.Name, $"end.{serial}");
                break;
        }
    }

    private static TimeSpan EstimateElapsed(PrinterStatus s)
    {
        if (s.Progress <= 0 || s.Progress >= 100 || s.RemainingMinutes <= 0) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(s.RemainingMinutes * 60.0 * s.Progress / (100 - s.Progress));
    }

    private void RequestNotificationsIfNeeded()
    {
        if (NotifyFinished || NotifyFailed || NotifyPaused || NotifyStarted) _notifier.RequestAuthorization();
    }

    // MARK: - Widgets

    public SharedSnapshot SnapshotOf(PrinterSession session)
    {
        var link = session.Phase switch
        {
            ConnectionPhase.Online => SharedSnapshot.LinkState.Online,
            ConnectionPhase.Connecting => session.Status is null ? SharedSnapshot.LinkState.Connecting : SharedSnapshot.LinkState.Online,
            ConnectionPhase.Unauthorized => SharedSnapshot.LinkState.Unauthorized,
            _ => SharedSnapshot.LinkState.Offline,
        };
        return new SharedSnapshot
        {
            Serial = session.Id, PrinterName = session.Config.Name, Model = session.Config.ModelName, Link = link, Status = session.Status,
            LastJob = History.FirstOrDefault(j => j.PrinterSerial == session.Id && j.Outcome != JobOutcome.Running), SavedAt = DateTime.UtcNow,
        };
    }

    /// <summary>The current printer as a widget would show it.</summary>
    public SharedSnapshot Snapshot => Current is { } current ? SnapshotOf(current) : SharedSnapshot.Unconfigured();

    public SharedFleet Fleet => new(_sessions.Select(SnapshotOf).ToList(), MenuBarSession?.Id, DateTime.UtcNow);

    // MARK: - Fleet

    /// <summary>Every printer, the ones that need someone first, then by soonest finish, then by name.</summary>
    public List<PrinterSession> FleetSessions => _sessions
        .OrderBy(s => s.AttentionRank.Group).ThenBy(s => s.AttentionRank.Minutes)
        .ThenBy(s => s.Config.Name, NaturalStringComparer.Instance).ToList();

    /// <summary>Only printers that are currently active (printing, preparing, paused) or need attention.</summary>
    public List<PrinterSession> ActiveSessions => FleetSessions
        .Where(s => s.IsActiveOrNeedsAttention).ToList();

    /// <summary>Printers to show in the fleet view, respecting <see cref="FleetActiveOnly"/>.</summary>
    public List<PrinterSession> DisplayedFleetSessions =>
        FleetActiveOnly ? ActiveSessions : FleetSessions;

    /// <summary>"6 printing · 1 paused · 2 finished · 1 offline".</summary>
    public string FleetSummary
    {
        get
        {
            var counts = new List<string>();
            void Count(string title, Func<PrinterSession, bool> match)
            {
                var n = _sessions.Count(match);
                if (n > 0) counts.Add($"{n} {title}");
            }
            Count("need attention", s => s.AttentionRank.Group == 0);
            Count("printing", s => s.IsOnline && s.Status?.State is JobState.Printing or JobState.Preparing);
            Count("paused", s => s.IsOnline && s.Status?.State == JobState.Paused);
            Count("finished", s => s.IsOnline && s.Status?.State == JobState.Finished);
            Count("ready", s => s.IsOnline && s.Status?.State == JobState.Idle);
            Count("offline", s => !s.IsOnline);
            return string.Join(" · ", counts);
        }
    }

    // MARK: - Demo

    private void StartDemo(DemoDriver.Scenario scenario)
    {
        IsDemo = true;
        if (scenario == DemoDriver.Scenario.Unconfigured) { NotifyChanged(); return; }
        // Not saved: a demo run must not replace the real printers' settings.
        var printers = new List<(PrinterConfig, DemoDriver.Scenario)>
        {
            (new PrinterConfig { Host = "192.168.1.50", Serial = "22E00A000000000", Name = "P2S Workshop", ModelCode = "N7" }, scenario),
        };
        if (AppArguments.Current.Has("--fleet"))
        {
            printers.Add((new PrinterConfig { Host = "192.168.1.60", Serial = "SMU-192-168-1-60", Name = "Snapmaker U1", ModelCode = "SM-U1", Protocol = PrinterProtocol.Moonraker }, DemoDriver.Scenario.Printing));
            printers.Add((new PrinterConfig { Host = "192.168.1.51", Serial = "00M00A000000000", Name = "X1C Garage", ModelCode = "BL-P001" }, DemoDriver.Scenario.Idle));
            printers.Add((new PrinterConfig { Host = "192.168.1.52", Serial = "039000000000000", Name = "A1 Desk", ModelCode = "N2S" }, DemoDriver.Scenario.Paused));
            printers.Add((new PrinterConfig { Host = "192.168.1.53", Serial = "094000000000000", Name = "H2D Studio", ModelCode = "O1D" }, DemoDriver.Scenario.Printing));
            printers.Add((new PrinterConfig { Host = "192.168.1.54", Serial = "030000000000000", Name = "A1 mini Shelf", ModelCode = "N1" }, DemoDriver.Scenario.Finished));
            printers.Add((new PrinterConfig { Host = "192.168.1.55", Serial = "01P000000000000", Name = "P1S Office", ModelCode = "C12" }, DemoDriver.Scenario.Printing));
        }
        for (var i = 0; i < printers.Count; i++) Add(printers[i].Item1).StartDemo(printers[i].Item2, i);
        SelectedSerial = _sessions.FirstOrDefault()?.Id;
        History = DemoDriver.SampleHistory(_sessions.Select(s => s.Id).ToList());
        NotifyChanged();
    }
}

/// <summary>Orders "Printer 2" before "Printer 10", ignoring case, like Finder's <c>localizedStandardCompare</c>.</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        if (a is null || b is null) return string.Compare(a, b, StringComparison.Ordinal);
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                var c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                var c = string.Compare(a[i].ToString(), b[j].ToString(), StringComparison.CurrentCultureIgnoreCase);
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
