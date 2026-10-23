using System.Text.Json;
using System.Text.Json.Serialization;

namespace BambuBar.Models;

/// <summary>What the printer is doing, folded from <c>print.gcode_state</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JobState>))]
public enum JobState { Idle, Preparing, Printing, Paused, Finished, Failed }

public static class JobStateExtensions
{
    public static JobState FromGcodeState(string gcodeState) => gcodeState.ToUpperInvariant() switch
    {
        "PREPARE" or "SLICING" => JobState.Preparing,
        "RUNNING" => JobState.Printing,
        "PAUSE" => JobState.Paused,
        "FINISH" => JobState.Finished,
        "FAILED" => JobState.Failed,
        _ => JobState.Idle,
    };

    /// <summary>A job is on the plate and not over yet.</summary>
    public static bool IsActive(this JobState state) =>
        state is JobState.Preparing or JobState.Printing or JobState.Paused;

    public static string Title(this JobState state) => state switch
    {
        JobState.Idle => "Ready",
        JobState.Preparing => "Preparing",
        JobState.Printing => "Printing",
        JobState.Paused => "Paused",
        JobState.Finished => "Finished",
        JobState.Failed => "Failed",
        _ => "",
    };
}

/// <summary>The four motion profiles (<c>spd_lvl</c>).</summary>
public enum SpeedLevel { Silent = 1, Standard = 2, Sport = 3, Ludicrous = 4 }

public static class SpeedLevelExtensions
{
    public static readonly SpeedLevel[] All = [SpeedLevel.Silent, SpeedLevel.Standard, SpeedLevel.Sport, SpeedLevel.Ludicrous];

    public static string Title(this SpeedLevel level) => level.ToString();

    /// <summary>Nominal speed relative to Standard, as Bambu Studio shows it.</summary>
    public static int Percent(this SpeedLevel level) => level switch
    {
        SpeedLevel.Silent => 50,
        SpeedLevel.Standard => 100,
        SpeedLevel.Sport => 124,
        SpeedLevel.Ludicrous => 166,
        _ => 100,
    };
}

public sealed record Temperature(double Current, double Target)
{
    /// <summary>Zero target means the heater is off.</summary>
    [JsonIgnore] public bool IsHeating => Target > 0;
    /// <summary>Within a couple of degrees of the target.</summary>
    [JsonIgnore] public bool IsAtTarget => Target > 0 && Math.Abs(Current - Target) <= 2;
}

public readonly record struct IntRange(int Low, int High);

/// <summary>One filament slot: an AMS tray or the external spool holder.</summary>
public sealed record Spool
{
    public const int ExternalId = 254;

    /// <summary>Global slot number: <c>unit * 4 + slot</c> for AMS units 0-3, <c>128 + n</c> for AMS HT, 254 for the external spool.</summary>
    public int Id { get; set; }
    public int Unit { get; set; }
    public int Slot { get; set; }
    /// <summary>"PLA", "PETG"…; empty when the slot is empty or unknown.</summary>
    public string Material { get; set; } = "";
    /// <summary>"PLA Matte"…; empty for third-party spools.</summary>
    public string Name { get; set; } = "";
    /// <summary><c>RRGGBBAA</c>.</summary>
    public string Color { get; set; } = "00000000";
    /// <summary>More than one entry for multicolour spools.</summary>
    public ValueList<string> Colors { get; set; } = [];
    /// <summary>Percent remaining, null when the printer can't tell (non-RFID spools).</summary>
    public int? Remain { get; set; }
    /// <summary>Net filament weight of a full spool in grams (<c>tray_weight</c>), from the Bambu RFID tag.</summary>
    public int? Weight { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsBambu { get; set; }
    /// <summary>The Bambu Studio filament preset set for the slot (<c>tray_info_idx</c>, "GFA01").</summary>
    public string? Preset { get; set; }
    /// <summary>"Bambu Lab" for RFID spools, else the maker of the preset the slot is set to ("SUNLU").</summary>
    public string? Brand { get; set; }
    /// <summary>Bambu Lab's colour name ("Grass Green"), for their spools and colours picked from their list.</summary>
    public string? ColorName { get; set; }
    /// <summary>Nozzle temperatures the filament prints at, from the tag or the preset.</summary>
    public IntRange? NozzleRange { get; set; }

    [JsonIgnore] public bool IsExternal => Id == ExternalId;

    [JsonIgnore]
    public string RemainText => !IsLoaded ? "Empty" : Remain is int r ? $"{r}%" : "—";

    [JsonIgnore] public string DisplayName => Name.Length == 0 ? Material : Name;

    /// <summary>Filament left in grams: the full spool's weight from its tag times the share the AMS measured left.</summary>
    [JsonIgnore]
    public int? GramsLeft => IsLoaded && Remain is int r && Weight is int w && w > 0
        ? (int)Math.Round(w * r / 100.0, MidpointRounding.AwayFromZero)
        : null;

    /// <summary>"≈ 810 g" when the weight is known, else the percentage.</summary>
    [JsonIgnore] public string AmountText => GramsLeft is int g ? Format.Grams(g) : RemainText;

    /// <summary>"81% · ≈ 810 g", for tooltips.</summary>
    [JsonIgnore]
    public string RemainDetail
    {
        get
        {
            if (!IsLoaded) return "Empty";
            if (Remain is not int r) return "Not measured";
            return GramsLeft is int g ? $"{r}% · {Format.Grams(g)}" : $"{r}%";
        }
    }

    /// <summary>"A1", "A2"… like the printer's screen; "Ext" for the external spool.</summary>
    [JsonIgnore]
    public string Label
    {
        get
        {
            if (IsExternal) return "Ext";
            if (Unit >= 128) return $"HT{Unit - 127}";
            return $"{(char)(65 + Math.Min(Unit, 25))}{Slot + 1}";
        }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<AmsKind>))]
public enum AmsKind { Ams, Lite, Pro2, Ht }

public static class AmsKindExtensions
{
    public static string Title(this AmsKind kind) => kind switch
    {
        AmsKind.Ams => "AMS",
        AmsKind.Lite => "AMS Lite",
        AmsKind.Pro2 => "AMS 2 Pro",
        AmsKind.Ht => "AMS HT",
        _ => "AMS",
    };
}

public sealed record AmsUnit
{
    public int Id { get; set; }
    public AmsKind Kind { get; set; }
    /// <summary>Relative humidity in percent (<c>humidity_raw</c>), when the unit reports it.</summary>
    public int? Humidity { get; set; }
    /// <summary>1 (dry) to 5 (wet), Bambu Studio's scale.</summary>
    public int? HumidityLevel { get; set; }
    public double? Temperature { get; set; }
    public int DryingMinutesLeft { get; set; }
    public ValueList<Spool> Spools { get; set; } = [];

    /// <summary>"AMS A", "AMS 2 Pro B"…</summary>
    [JsonIgnore]
    public string Title => Id >= 128 ? $"{Kind.Title()} {Id - 127}" : $"{Kind.Title()} {(char)(65 + Math.Min(Id, 25))}";
}

/// <summary>Lower is more severe, as in Bambu's HMS levels.</summary>
public enum IssueSeverity { Fatal = 1, Serious = 2, Common = 3, Info = 4 }

public static class IssueSeverityExtensions
{
    public static string Title(this IssueSeverity severity) => severity switch
    {
        IssueSeverity.Fatal => "Error",
        IssueSeverity.Serious => "Warning",
        IssueSeverity.Common => "Notice",
        _ => "Info",
    };
}

/// <summary>A Health Management System message or the current print error.</summary>
public sealed record PrinterIssue(string Id, IssueSeverity Severity, string Message, string? WikiUrl);

/// <summary>Everything Bambu Bar shows about the printer, derived from the merged MQTT report.</summary>
public sealed record PrinterStatus
{
    public JobState State { get; set; } = JobState.Idle;
    public int? StageId { get; set; }
    public string JobName { get; set; } = "";
    public int Progress { get; set; }
    public int RemainingMinutes { get; set; }
    public int Layer { get; set; }
    public int TotalLayers { get; set; }
    public Temperature Nozzle { get; set; } = new(0, 0);
    public Temperature Bed { get; set; } = new(0, 0);
    public double? Chamber { get; set; }
    /// <summary>The chamber heater's target on models that heat the chamber (H2, X1E, X2D); zero when off.</summary>
    public double? ChamberTarget { get; set; }
    /// <summary>The left nozzle on two-nozzle models (<see cref="Nozzle"/> is the right one, as Bambu Studio numbers them).</summary>
    public Temperature? SecondNozzle { get; set; }
    /// <summary>Fan speeds in percent.</summary>
    public int? PartFan { get; set; }
    public int? AuxFan { get; set; }
    public int? ExhaustFan { get; set; }
    public int? HeatbreakFan { get; set; }
    public SpeedLevel Speed { get; set; } = SpeedLevel.Standard;
    public bool? ChamberLightOn { get; set; }
    public bool? DoorOpen { get; set; }
    public int? WifiSignal { get; set; }
    public ValueList<AmsUnit> Ams { get; set; } = [];
    public Spool? ExternalSpool { get; set; }
    public int? ActiveSpoolId { get; set; }
    public ValueList<PrinterIssue> Issues { get; set; } = [];
    /// <summary>The printer refuses unsigned print commands (Developer Mode off).</summary>
    public bool ControlsLocked { get; set; }
    public string? Firmware { get; set; }
    public double? NozzleDiameter { get; set; }
    public string? NozzleType { get; set; }
    public bool? LiveViewEnabled { get; set; }
    public bool? HasSdCard { get; set; }
    public int? PlateIndex { get; set; }
    public bool UpdateAvailable { get; set; }
    /// <summary>The printer's id for the current job (<c>task_id</c>, else <c>subtask_id</c>).</summary>
    public string? TaskId { get; set; }
    /// <summary>The job ended because someone stopped it (print_error 0300_400C), not because of a fault.</summary>
    public bool Canceled { get; set; }
    /// <summary>When this status was last refreshed from the printer (UTC).</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>"Canceled" for a job someone stopped, otherwise the state's own title.</summary>
    [JsonIgnore] public string StateTitle => State == JobState.Failed && Canceled ? "Canceled" : State.Title();

    [JsonIgnore]
    public string? StageText => StageId is int id && id != 255 && id != -1 && StageNames.TryGetValue(id, out var name) ? name : null;

    /// <summary>The line under the job name: "Heating hotend", "Paused: filament runout", "Layer 170 of 305"…</summary>
    [JsonIgnore]
    public string DetailText
    {
        get
        {
            switch (State)
            {
                case JobState.Printing:
                    if (StageText is { } stage && StageId != 0) return stage;
                    if (TotalLayers > 0) return $"Layer {Layer} of {TotalLayers}";
                    return "Printing";
                case JobState.Preparing:
                    return StageText ?? "Preparing";
                case JobState.Paused:
                    if (StageText is { } paused && paused.StartsWith("Paused", StringComparison.Ordinal)) return paused;
                    return "Paused";
                case JobState.Finished:
                    return "Finished";
                case JobState.Failed:
                    return Canceled ? "Canceled" : Issues.FirstOrDefault()?.Message ?? "Print failed";
                default:
                    return StageText ?? "Ready to print";
            }
        }
    }

    [JsonIgnore]
    public Spool? ActiveSpool
    {
        get
        {
            if (ActiveSpoolId is not int id) return null;
            if (id == Spool.ExternalId) return ExternalSpool;
            return Ams.SelectMany(u => u.Spools).FirstOrDefault(s => s.Id == id);
        }
    }

    [JsonIgnore]
    public IEnumerable<Spool> AllSpools => Ams.SelectMany(u => u.Spools).Concat(ExternalSpool is null ? [] : [ExternalSpool]);

    [JsonIgnore]
    public PrinterIssue? MostSevereIssue => Issues.Count == 0 ? null : Issues.MinBy(i => (int)i.Severity);

    /// <summary>Estimated completion (UTC), from the remaining minutes at <see cref="UpdatedAt"/>.</summary>
    [JsonIgnore]
    public DateTime? EstimatedEnd => State.IsActive() && RemainingMinutes > 0 ? UpdatedAt.AddMinutes(RemainingMinutes) : null;

    /// <summary>Progress expected at <paramref name="date"/>, extrapolated linearly over the remaining time.
    /// Lets widgets advance between updates.</summary>
    public double ProjectedProgress(DateTime date)
    {
        var now = Progress / 100.0;
        if (State != JobState.Printing || EstimatedEnd is not DateTime end || end <= UpdatedAt) return now;
        var fraction = Math.Clamp((date - UpdatedAt).TotalSeconds / (end - UpdatedAt).TotalSeconds, 0, 1);
        return Math.Min(0.99, now + (1 - now) * fraction);
    }

    /// <summary>A deep copy, for code that changes a status it got from someone else.</summary>
    public PrinterStatus DeepClone() =>
        JsonSerializer.Deserialize<PrinterStatus>(JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions.Default), JsonOptions.Default)!;

    public static readonly IReadOnlyDictionary<int, string> StageNames = new Dictionary<int, string>
    {
        [0] = "Printing", [1] = "Auto bed leveling", [2] = "Preheating heatbed", [3] = "Vibration compensation",
        [4] = "Changing filament", [5] = "M400 pause", [6] = "Paused: filament ran out", [7] = "Heating hotend",
        [8] = "Calibrating extrusion", [9] = "Scanning bed surface", [10] = "Inspecting first layer",
        [11] = "Identifying build plate", [12] = "Calibrating Micro Lidar", [13] = "Homing toolhead",
        [14] = "Cleaning nozzle tip", [15] = "Checking extruder temperature", [16] = "Paused by user",
        [17] = "Paused: front cover fell off", [18] = "Calibrating Micro Lidar", [19] = "Calibrating flow ratio",
        [20] = "Paused: nozzle temperature fault", [21] = "Paused: heatbed temperature fault", [22] = "Unloading filament",
        [23] = "Paused: skipped steps", [24] = "Loading filament", [25] = "Calibrating motor noise",
        [26] = "Paused: AMS offline", [27] = "Paused: heatbreak fan too slow", [28] = "Paused: chamber temperature fault",
        [29] = "Cooling chamber", [30] = "Paused by G-code", [31] = "Motor noise showoff",
        [32] = "Paused: nozzle clumping", [33] = "Paused: cutter error", [34] = "Paused: first layer error",
        [35] = "Paused: nozzle clog", [36] = "Measuring motion precision", [37] = "Enhancing motion precision",
        [38] = "Measuring motion accuracy", [39] = "Calibrating nozzle offset", [40] = "High-temperature bed leveling",
        [41] = "Checking quick-release lever", [42] = "Checking door and top cover", [43] = "Calibrating laser",
        [44] = "Checking platform", [45] = "Checking BirdsEye camera", [46] = "Calibrating BirdsEye camera",
        [47] = "Bed leveling, phase 1", [48] = "Bed leveling, phase 2", [49] = "Heating chamber",
        [50] = "Cooling heatbed", [51] = "Printing calibration lines", [52] = "Checking material",
        [53] = "Calibrating Live View camera", [54] = "Waiting for heatbed temperature", [55] = "Checking material position",
        [56] = "Calibrating cutter offset", [57] = "Measuring surface", [58] = "Thermal preconditioning",
        [59] = "Homing blade holder", [60] = "Calibrating camera offset", [61] = "Calibrating blade holder",
        [62] = "Hotend pick-and-place test", [63] = "Waiting for chamber temperature", [64] = "Preparing hotend",
        [65] = "Calibrating clumping detection", [66] = "Purifying chamber air", [67] = "Measuring rotary attachment",
        [68] = "Moving to purge chute", [69] = "Cooling nozzle", [70] = "Centering toolhead",
        [71] = "Arc fitting", [72] = "Detecting hotend type", [73] = "Detecting build plate alignment",
        [74] = "Checking heatbed for objects", [75] = "Checking heatbed underside", [76] = "Pre-extruding",
        [77] = "Preparing AMS",
    };
}

/// <summary>The last job the printer ran, kept so an idle printer can still say how its last print went.</summary>
public sealed record JobRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public JobOutcome Outcome { get; set; }
    public int TotalLayers { get; set; }
    public string? FilamentColor { get; set; }
    public string? FilamentMaterial { get; set; }
    /// <summary>The printer's task id, to tell two jobs with the same name apart.</summary>
    public string? TaskId { get; set; }
    /// <summary>Which printer ran it.</summary>
    public string? PrinterSerial { get; set; }

    [JsonIgnore] public TimeSpan? Duration => EndedAt is DateTime end ? end - StartedAt : null;
}

[JsonConverter(typeof(JsonStringEnumConverter<JobOutcome>))]
public enum JobOutcome
{
    Running, Finished, Failed, Canceled,
    /// <summary>The app wasn't running when it ended, so how it ended isn't known.</summary>
    Ended,
}

public static class JobOutcomeExtensions
{
    public static string Title(this JobOutcome outcome) => outcome switch
    {
        JobOutcome.Running => "In progress",
        JobOutcome.Finished => "Finished",
        JobOutcome.Failed => "Failed",
        JobOutcome.Canceled => "Canceled",
        _ => "Ended",
    };
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions Indented = new(Default) { WriteIndented = true };
}
