using BambuBar.Models;
using BambuBar.Protocol;

namespace BambuBar.Services;

/// <summary>
/// <c>--demo [scenario] [--fleet]</c>: a simulated P2S (plus five more printers with <c>--fleet</c>), for screenshots and
/// for trying the app without a printer. Nothing is saved and no network is touched.
/// </summary>
public sealed class DemoDriver
{
    public enum Scenario { Printing, Preparing, Paused, Finished, Failed, Idle, Offline, Unconfigured }

    public static Scenario? RequestedScenario
    {
        get
        {
            var args = AppArguments.Current;
            if (!args.Has("--demo")) return null;
            return Enum.TryParse<Scenario>(args.Value("--demo"), true, out var s) ? s : Scenario.Printing;
        }
    }

    public event Action<PrinterStatus, bool>? StatusChanged;
    public event Action<ConnectionPhase>? PhaseChanged;

    private readonly Scenario _scenario;
    private readonly PrinterStatus _status = SamplePrinter.Status();
    private MainTimer? _timer;
    private int _tick;
    private static readonly Random Jitter = new();

    /// <summary><paramref name="variant"/> gives each simulated printer its own job and spools.</summary>
    public DemoDriver(Scenario scenario, int variant = 0)
    {
        _scenario = scenario;
        var s = _status;
        var spools = s.Ams[0].Spools;
        switch (variant)
        {
            case 1:
                s.JobName = "Gridfinity Baseplate 4x4";
                Recolor(spools[0], "DE4343", "Scarlet Red", "PLA Matte", "GFA01");
                s.Ams[0].Kind = AmsKind.Ams;
                s.ActiveSpoolId = 0;
                s.ExternalSpool = null;
                break;
            case 2:
                s.JobName = "Articulated Dragon";
                s.Ams[0].Kind = AmsKind.Lite;
                Recolor(spools[1], "8671CB", "Purple", "PLA Silk+", "GFA06");
                s.ActiveSpoolId = 1;
                s.Progress = 31;
                s.Layer = 88;
                s.TotalLayers = 284;
                s.RemainingMinutes = 147;
                s.Chamber = null;
                s.ExternalSpool = null;
                break;
            case 3:
                s.JobName = "Two-tone Vase";
                Recolor(spools[0], "FFFFFF", "Jade White", "PLA Basic", "GFA00");
                Recolor(spools[1], "00AE42", "Bambu Green", "PLA Basic", "GFA00");
                s.ActiveSpoolId = 1;
                s.Progress = 44;
                s.Layer = 132;
                s.TotalLayers = 300;
                s.RemainingMinutes = 188;
                s.SecondNozzle = new Temperature(170, 170);
                s.Chamber = 41;
                break;
            case 4:
                s.JobName = "Keychain Batch";
                s.Ams[0].Kind = AmsKind.Lite;
                Recolor(spools[3], "E8AFCF", "Sakura Pink", "PLA Matte", "GFA01");
                s.ActiveSpoolId = 3;
                s.Chamber = null;
                s.ExternalSpool = null;
                break;
            case 5:
                s.JobName = "Cable Clips x24";
                s.Ams[0].Kind = AmsKind.Ams;
                Recolor(spools[2], "0056B8", "Cobalt Blue", "PLA Basic", "GFA00");
                s.ActiveSpoolId = 2;
                s.Progress = 83;
                s.Layer = 166;
                s.TotalLayers = 200;
                s.RemainingMinutes = 18;
                s.ExternalSpool = null;
                break;
        }
    }

    /// <summary>A Bambu Lab spool of another colour, as its tag would report it.</summary>
    private static void Recolor(Spool spool, string hex, string colorName, string product, string preset)
    {
        spool.Color = hex + "FF";
        spool.Colors = [spool.Color];
        spool.ColorName = colorName;
        spool.Name = product;
        spool.Material = "PLA";
        spool.Preset = preset;
        spool.Brand = "Bambu Lab";
        spool.IsBambu = true;
        spool.Weight = 1000;
        spool.Remain ??= 72;
    }

    public void Start()
    {
        switch (_scenario)
        {
            case Scenario.Unconfigured:
                PhaseChanged?.Invoke(ConnectionPhase.IdlePhase);
                return;
            case Scenario.Offline:
                PhaseChanged?.Invoke(new ConnectionPhase.Offline("The printer isn't reachable on the network."));
                return;
        }
        PhaseChanged?.Invoke(ConnectionPhase.OnlinePhase);
        Shape();
        Emit();
        // Snapshots script every status themselves.
        if (_scenario is not (Scenario.Printing or Scenario.Preparing) || AppArguments.Current.Has("--snapshot")) return;
        _timer = MainTimer.Every(TimeSpan.FromSeconds(2), Advance);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Shape()
    {
        var s = _status;
        switch (_scenario)
        {
            case Scenario.Preparing:
                s.State = JobState.Preparing;
                s.StageId = 7;
                s.Progress = 0;
                s.Layer = 0;
                s.RemainingMinutes = 168;
                s.Nozzle = new Temperature(164, 220);
                break;
            case Scenario.Paused:
                s.State = JobState.Paused;
                s.StageId = 6;
                s.Issues = [new PrinterIssue("HMS_0700_2300_0001_0004", IssueSeverity.Serious,
                    "AMS A slot 4 filament has run out. Please insert a new filament.", HmsCatalog.WikiUrl(""))];
                break;
            case Scenario.Finished:
                s.State = JobState.Finished;
                s.StageId = 255;
                s.Progress = 100;
                s.RemainingMinutes = 0;
                s.Layer = 305;
                s.Nozzle = new Temperature(46, 0);
                s.Bed = new Temperature(41, 0);
                s.ActiveSpoolId = null;
                break;
            case Scenario.Failed:
                s.State = JobState.Failed;
                s.StageId = 255;
                s.Nozzle = new Temperature(180, 0);
                s.Issues = [new PrinterIssue("0300_4006", IssueSeverity.Fatal,
                    "The nozzle is clogged with filament. The print has been stopped.", HmsCatalog.WikiUrl(""))];
                break;
            case Scenario.Idle:
                s.State = JobState.Idle;
                s.StageId = 255;
                s.JobName = "";
                s.Progress = 0;
                s.RemainingMinutes = 0;
                s.Layer = 0;
                s.TotalLayers = 0;
                s.Nozzle = new Temperature(27, 0);
                s.Bed = new Temperature(25, 0);
                s.Chamber = 24;
                s.PartFan = 0;
                s.AuxFan = 0;
                s.ActiveSpoolId = null;
                s.ChamberLightOn = false;
                break;
        }
    }

    private void Advance()
    {
        var s = _status;
        _tick++;
        if (s.State == JobState.Preparing)
        {
            s.Nozzle = s.Nozzle with { Current = Math.Min(s.Nozzle.Target, s.Nozzle.Current + 9) };
            if (s.Nozzle.IsAtTarget) { s.State = JobState.Printing; s.StageId = 0; }
        }
        else if (s.State == JobState.Printing && _tick % 2 == 0 && s.Progress < 99)
        {
            s.Progress++;
            s.Layer = Math.Min(s.TotalLayers, (int)(s.TotalLayers * s.Progress / 100.0));
            s.RemainingMinutes = Math.Max(1, s.RemainingMinutes - 2);
            s.UpdatedAt = DateTime.UtcNow;
        }
        if (s.Nozzle.Target > 0) s.Nozzle = s.Nozzle with { Current = s.Nozzle.Target + new[] { -1, 0, 0, 1 }[Jitter.Next(4)] };
        Emit();
    }

    private void Emit(bool canceled = false)
    {
        if (_status.State is JobState.Printing or JobState.Preparing) _status.UpdatedAt = DateTime.UtcNow;
        // A copy, so the session's old and new statuses stay apart.
        StatusChanged?.Invoke(_status.DeepClone(), canceled);
    }

    public void Pause() { _status.State = JobState.Paused; _status.StageId = 16; Emit(); }
    public void Resume() { _status.State = JobState.Printing; _status.StageId = 0; Emit(); }

    public void StopPrint()
    {
        _status.State = JobState.Failed;
        _status.StageId = 255;
        _status.Issues = [];
        _status.Canceled = true;
        Emit(canceled: true);
    }

    public void SetLight(bool on) { _status.ChamberLightOn = on; Emit(); }
    public void SetSpeed(SpeedLevel level) { _status.Speed = level; Emit(); }

    public static List<JobRecord> SampleHistory(IReadOnlyList<string> serials)
    {
        var now = DateTime.UtcNow;
        JobRecord Job(string name, double hoursAgo, double minutes, JobOutcome outcome, string color, string material, int layers)
        {
            var end = now.AddHours(-hoursAgo);
            return new JobRecord
            {
                Name = name, StartedAt = end.AddMinutes(-minutes), EndedAt = end, Outcome = outcome, TotalLayers = layers,
                FilamentColor = color, FilamentMaterial = material,
            };
        }
        var jobs = new[]
        {
            Job("Cable Clip x12", 3.2, 48, JobOutcome.Finished, "1C1C1EFF", "PLA", 64),
            Job("Benchy", 20, 41, JobOutcome.Finished, "E2763AFF", "PLA", 240),
            Job("Headphone Stand", 27, 312, JobOutcome.Finished, "F4EEE6FF", "PLA", 812),
            Job("Gridfinity Bin 2x3", 49, 12, JobOutcome.Canceled, "2E7D5BFF", "PETG", 140),
            Job("Planter Spiral", 75, 95, JobOutcome.Failed, "2E7D5BFF", "PETG", 410),
            Job("Desk Hook", 98, 36, JobOutcome.Finished, "1C1C1EFF", "PLA", 120),
        };
        // Spread over the printers, oldest first per printer like real history.
        for (var i = 0; i < jobs.Length; i++) jobs[i].PrinterSerial = serials.Count == 0 ? null : serials[i % serials.Count];
        return [.. jobs];
    }
}
