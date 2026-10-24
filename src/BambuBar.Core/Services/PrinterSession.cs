using BambuBar.Models;
using BambuBar.Protocol;

namespace BambuBar.Services;

/// <summary>
/// One configured printer: its connection, live status and the commands in flight.
/// <see cref="PrinterStore"/> owns the sessions and handles what spans printers (history, notifications, widgets).
/// Lives on the main thread.
/// </summary>
public sealed class PrinterSession : IDisposable
{
    /// <summary>The serial number, which never changes for a session.</summary>
    public string Id { get; }
    public PrinterConfig Config { get; set; }
    public ConnectionPhase Phase { get; private set; } = ConnectionPhase.IdlePhase;
    public PrinterStatus? Status { get; private set; }
    /// <summary>Commands sent but not yet reflected in a report, so the controls respond immediately.</summary>
    public bool? PendingLight { get; private set; }
    public SpeedLevel? PendingSpeed { get; private set; }
    /// <summary>More codes to try if the printer rejects the current one (a slicer may hold an old and a new one).</summary>
    public List<string> FallbackCodes { get; set; } = [];
    /// <summary>False until the first report after connecting, so state already in progress doesn't notify.</summary>
    public bool HasBaseline { get; private set; }
    /// <summary>When the printer last sent anything, to the second ("Updated 3s ago").</summary>
    public DateTime? LastHeard { get; private set; }

    public event Action<PrinterSession, ConnectionPhase>? PhaseChanged;
    /// <summary>Old status, new status, whether the job was canceled.</summary>
    public event Action<PrinterSession, PrinterStatus?, PrinterStatus, bool>? StatusChanged;
    public event Action<PrinterSession, string, bool, string?>? Acknowledged;
    public event Action<PrinterSession>? Heartbeat;
    /// <summary>Anything a view shows changed.</summary>
    public event Action? Changed;

    private readonly PrinterConnection _connection = new();
    private readonly MoonrakerConnection _moonraker = new();
    private readonly ICredentialStore _credentials;
    private DemoDriver? _demo;
    private bool _insecure;
    private MainTimer? _pendingTimer;

    public PrinterSession(PrinterConfig config, ICredentialStore credentials)
    {
        Id = config.Serial;
        Config = config;
        _credentials = credentials;
        _connection.PhaseChanged += OnPhaseChanged;
        _connection.StatusChanged += (status, doc) => Received(status, ReportParser.WasCanceled(doc));
        _connection.Acknowledged += OnBackendAcknowledged;
        _connection.Heartbeat += OnBackendHeartbeat;

        _moonraker.PhaseChanged += OnPhaseChanged;
        _moonraker.StatusChanged += Received;
        _moonraker.Acknowledged += OnBackendAcknowledged;
        _moonraker.Heartbeat += OnBackendHeartbeat;
    }

    private void OnBackendAcknowledged(string command, bool ok, string? reason)
    {
        if (!ok) { PendingLight = null; PendingSpeed = null; Changed?.Invoke(); }
        Acknowledged?.Invoke(this, command, ok, reason);
    }

    private void OnBackendHeartbeat()
    {
        // Whole seconds are enough for the label and spare the UI an update per message.
        if (LastHeard is not DateTime heard || DateTime.UtcNow - heard >= TimeSpan.FromSeconds(1))
        {
            LastHeard = DateTime.UtcNow;
            Changed?.Invoke();
        }
        Heartbeat?.Invoke(this);
    }

    // MARK: - Lifecycle

    public void Start(string accessCode, IEnumerable<string>? alternatives = null, bool insecure = false)
    {
        _insecure = insecure;
        FallbackCodes = (alternatives ?? []).Where(c => c != accessCode).ToList();
        Status = null;
        HasBaseline = false;
        if (Config.Protocol == PrinterProtocol.Moonraker)
        {
            _connection.Stop();
            _moonraker.Start(Config, accessCode);
        }
        else
        {
            _moonraker.Stop();
            _connection.Start(Config, accessCode, insecure);
        }
        Changed?.Invoke();
    }

    /// <summary>Runs a simulated printer instead of a connection (<c>--demo</c>).</summary>
    public void StartDemo(DemoDriver.Scenario scenario, int variant)
    {
        var driver = new DemoDriver(scenario, variant);
        driver.StatusChanged += (status, canceled) => Received(status, canceled);
        driver.PhaseChanged += OnPhaseChanged;
        _demo = driver;
        driver.Start();
    }

    /// <summary>Shows a scripted status, as if the printer had reported it (snapshots).</summary>
    public void ShowDemoStatus(PrinterStatus status)
    {
        Phase = ConnectionPhase.OnlinePhase;
        Received(status, status.Canceled);
    }

    public void Stop()
    {
        _connection.Stop();
        _moonraker.Stop();
        _demo?.Stop();
        Phase = ConnectionPhase.IdlePhase;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _pendingTimer?.Dispose();
        _demo?.Stop();
        _connection.Dispose();
        _moonraker.Dispose();
    }

    public void Refresh()
    {
        if (_demo is not null) return;
        if (Config.Protocol == PrinterProtocol.Moonraker) _moonraker.Refresh();
        else _connection.Refresh();
    }

    /// <summary>Reconnects with the stored code, e.g. after the printer moved to a new IP address.</summary>
    public void Restart()
    {
        if (_demo is not null) return;
        var code = _credentials.AccessCode(Config.Serial) ?? "";
        if (Config.Protocol == PrinterProtocol.Moonraker)
            _moonraker.Start(Config, code);
        else if (code.Length > 0)
            _connection.Start(Config, code, _insecure);
    }

    // MARK: - Derived

    /// <summary>A live status is on hand: connected, or briefly reconnecting after a drop.</summary>
    public bool IsOnline => Status is not null && Phase is ConnectionPhase.Online or ConnectionPhase.Connecting;
    public bool ChamberLightOn => PendingLight ?? Status?.ChamberLightOn ?? false;
    public SpeedLevel Speed => PendingSpeed ?? Status?.Speed ?? SpeedLevel.Standard;
    public string? AccessCode => _credentials.AccessCode(Config.Serial);

    public string LinkSummary => Phase switch
    {
        ConnectionPhase.Idle => "Not connected",
        ConnectionPhase.Connecting => "Connecting…",
        ConnectionPhase.Online => "Connected",
        ConnectionPhase.Offline => "Offline",
        ConnectionPhase.Unauthorized => Config.Protocol == PrinterProtocol.Moonraker ? "API key rejected" : "Access code rejected",
        ConnectionPhase.CertificateRejected => "Certificate not trusted",
        _ => "",
    };

    public string? LinkDetail => Phase switch
    {
        ConnectionPhase.Offline offline => offline.Reason,
        ConnectionPhase.Unauthorized => Config.Protocol == PrinterProtocol.Moonraker
            ? "Moonraker rejected the request. Check the API key if authentication is enabled in moonraker.conf."
            : "Check the access code on the printer's screen (Settings → Network) and enter it again.",
        ConnectionPhase.CertificateRejected rejected => $"The printer's TLS certificate couldn't be verified: {rejected.Reason}",
        _ => null,
    };

    /// <summary>"Printing · 64%", "Ready", "Offline": one line for menus and lists.</summary>
    public string ShortState
    {
        get
        {
            if (Status is not { } s || !IsOnline) return LinkSummary;
            return s.State.IsActive() ? $"{s.State.Title()} · {s.Progress}%" : s.StateTitle;
        }
    }

    /// <summary>Where the printer sorts in the fleet: what needs a person first, then what finishes soonest.</summary>
    public (int Group, int Minutes) AttentionRank
    {
        get
        {
            if (Status is not { } s || !IsOnline) return Phase is ConnectionPhase.Connecting ? (5, 0) : (6, 0);
            if (s.State == JobState.Failed && !s.Canceled) return (0, 0);
            if (s.State == JobState.Paused || (s.MostSevereIssue is { } issue && issue.Severity <= IssueSeverity.Serious)) return (1, 0);
            return s.State switch
            {
                JobState.Printing or JobState.Preparing => (2, s.RemainingMinutes),
                JobState.Finished => (3, 0),
                _ => (4, 0),
            };
        }
    }

    public bool NeedsAttention => AttentionRank.Group <= 1;

    /// <summary>True when the printer is actively printing, preparing, paused, or needs attention.</summary>
    public bool IsActiveOrNeedsAttention =>
        NeedsAttention || (IsOnline && Status?.State.IsActive() == true);

    /// <summary>The line under the name: what went wrong, else the job, else the model.</summary>
    public string FleetDetail
    {
        get
        {
            if (Status is not { } s || !IsOnline) return $"{Config.ModelName} · {LinkSummary}";
            if (s.MostSevereIssue is { } issue && issue.Severity <= IssueSeverity.Serious) return issue.Message;
            if (s.JobName.Length > 0 && s.State != JobState.Idle) return s.JobName;
            return $"{Config.ModelName} · {s.StateTitle}";
        }
    }

    // MARK: - Commands

    public void Pause()
    {
        if (_demo is not null) _demo.Pause();
        else if (Config.Protocol == PrinterProtocol.Moonraker) _moonraker.Pause();
        else _connection.Pause();
    }

    public void Resume()
    {
        if (_demo is not null) _demo.Resume();
        else if (Config.Protocol == PrinterProtocol.Moonraker) _moonraker.Resume();
        else _connection.Resume();
    }

    public void StopPrint()
    {
        if (_demo is not null) _demo.StopPrint();
        else if (Config.Protocol == PrinterProtocol.Moonraker) _moonraker.StopPrint();
        else _connection.StopPrint();
    }

    public void ToggleLight()
    {
        var on = !ChamberLightOn;
        PendingLight = on;
        if (_demo is not null) _demo.SetLight(on);
        else if (Config.Protocol == PrinterProtocol.Moonraker) _moonraker.SetChamberLight(on);
        else _connection.SetChamberLight(on);
        ClearPendingSoon();
        Changed?.Invoke();
    }

    public void SetSpeed(SpeedLevel level)
    {
        PendingSpeed = level;
        if (_demo is not null) _demo.SetSpeed(level);
        else if (Config.Protocol == PrinterProtocol.Moonraker) _moonraker.SetSpeed(level);
        else _connection.SetSpeed(level);
        ClearPendingSoon();
        Changed?.Invoke();
    }

    private void ClearPendingSoon()
    {
        _pendingTimer?.Dispose();
        _pendingTimer = MainTimer.After(TimeSpan.FromSeconds(6), () =>
        {
            PendingLight = null;
            PendingSpeed = null;
            Changed?.Invoke();
        });
    }

    // MARK: - Updates

    private void OnPhaseChanged(ConnectionPhase phase)
    {
        if (phase is ConnectionPhase.Unauthorized && FallbackCodes.Count > 0)
        {
            // Try the slicer's other stored code before telling anyone.
            var next = FallbackCodes[0];
            FallbackCodes.RemoveAt(0);
            _credentials.SetAccessCode(next, Config.Serial);
            _connection.Start(Config, next, _insecure);
            return;
        }
        if (phase is ConnectionPhase.Online) FallbackCodes = [];
        Phase = phase;
        PhaseChanged?.Invoke(this, phase);
        Changed?.Invoke();
    }

    private void Received(PrinterStatus reported, bool canceled)
    {
        // A copy: the connection keeps the status it reported to compare the next one against.
        var fresh = reported with { };
        fresh.Fit(Config.Model);
        var old = Status;
        Status = fresh;
        LastHeard = DateTime.UtcNow;
        if (PendingLight is bool light && fresh.ChamberLightOn == light) PendingLight = null;
        if (PendingSpeed is SpeedLevel level && fresh.Speed == level) PendingSpeed = null;
        StatusChanged?.Invoke(this, old, fresh, canceled);
        HasBaseline = true;
        Changed?.Invoke();
    }
}
