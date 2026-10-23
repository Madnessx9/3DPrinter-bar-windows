namespace BambuBar.Models;

/// <summary>Which drawing fits a printer model.</summary>
public enum PrinterShape
{
    /// <summary>P2S, P1S, X1 family, X2D: a closed box with a glass door.</summary>
    Enclosed,
    /// <summary>H2 family: a wider closed box with a dual-nozzle toolhead.</summary>
    Wide,
    /// <summary>P1P: the CoreXY frame without panels.</summary>
    OpenFrame,
    /// <summary>A1 and A2L: an open bed-slinger under an arch-shaped gantry.</summary>
    BedSlinger,
    /// <summary>A1 mini: a bed-slinger with a single Z column.</summary>
    Mini,
}

public static class PrinterShapeExtensions
{
    /// <summary>From a model name ("P2S", "A1 mini") or SSDP code ("N7", "N1").</summary>
    public static PrinterShape FromModel(string? model)
    {
        if (PrinterModel.Named(model) is { } known) return known.Shape;
        var m = (model ?? "").ToLowerInvariant();
        if (m.Contains("mini") || m is "n1" or "a12" or "a04") return PrinterShape.Mini;
        if (m.StartsWith("a1") || m.StartsWith("a2") || m is "n2s" or "a11" or "n9") return PrinterShape.BedSlinger;
        if (m.StartsWith("h2") || m.StartsWith("o1") || m.StartsWith("o2")) return PrinterShape.Wide;
        if (m is "p1p" or "c11") return PrinterShape.OpenFrame;
        return PrinterShape.Enclosed;
    }

    public static bool IsBedSlinger(this PrinterShape shape) => shape is PrinterShape.BedSlinger or PrinterShape.Mini;

    /// <summary>Base name of this shape's renders and model ("enclosed-body.png", "enclosed.mesh").</summary>
    public static string AssetName(this PrinterShape shape) => shape switch
    {
        PrinterShape.Enclosed => "enclosed",
        PrinterShape.Wide => "wide",
        PrinterShape.OpenFrame => "openFrame",
        PrinterShape.BedSlinger => "bedSlinger",
        PrinterShape.Mini => "mini",
        _ => "enclosed",
    };
}

/// <summary>
/// What a Bambu Lab model has, so the app and widgets show only what the printer can report.
///
/// From the printer profiles Bambu Lab publishes with Bambu Studio: <c>resources/printers/&lt;model&gt;.json</c>
/// (<c>support_chamber</c>, <c>support_chamber_temp_display</c>, <c>support_chamber_temp_edit_range</c>, <c>support_aux_fan</c>,
/// <c>support_chamber_fan</c>, <c>support_ams_humidity</c>, <c>nozzle_temp_range</c>, <c>bed_temp_range</c>, the tool heads) and
/// <c>resources/profiles/BBL/machine/&lt;name&gt; 0.4 nozzle.json</c> (<c>printable_area</c>, <c>printable_height</c>).
/// </summary>
public sealed class PrinterModel
{
    /// <summary>Bambu's model id, as SSDP and Bambu Studio name it ("N7").</summary>
    public required string Code { get; init; }
    /// <summary>"P2S", "X1 Carbon"…</summary>
    public required string Name { get; init; }
    public required PrinterShape Shape { get; init; }
    /// <summary>The first three characters of its serial numbers.</summary>
    public required string SerialPrefix { get; init; }
    /// <summary>A closed chamber with a door.</summary>
    public bool IsEnclosed { get; init; }
    /// <summary>Reports a chamber temperature worth showing (the P1S has a chamber but no sensor).</summary>
    public bool HasChamberSensor { get; init; }
    /// <summary>Highest chamber temperature it can heat to; null without active chamber heating.</summary>
    public int? ChamberHeaterMax { get; init; }
    public bool HasAuxFan { get; init; }
    public bool HasChamberFan { get; init; }
    /// <summary>A switchable chamber light (none on the A series).</summary>
    public bool HasChamberLight => Shape != PrinterShape.BedSlinger && Shape != PrinterShape.Mini;
    /// <summary>The AMS reports humidity (not the AMS Lite of the A series).</summary>
    public bool HasAmsHumidity { get; init; }
    /// <summary>Only the AMS Lite fits (A1, A1 mini).</summary>
    public bool UsesAmsLite { get; init; }
    /// <summary>Two tool heads (H2D, H2C, X2D).</summary>
    public int NozzleCount { get; init; } = 1;
    public int NozzleMax { get; init; }
    public int BedMax { get; init; }
    /// <summary>Width × depth × height in millimetres.</summary>
    public (int X, int Y, int Z) BuildVolume { get; init; }
    public bool HasLidar { get; init; }
    /// <summary>Takes the laser and cutting modules (H2 family).</summary>
    public bool HasLaserModule { get; init; }

    public string BuildVolumeText => $"{BuildVolume.X} × {BuildVolume.Y} × {BuildVolume.Z} mm";

    public string ChamberText =>
        ChamberHeaterMax is int max ? $"Heated, up to {max} °C"
        : HasChamberSensor ? "Enclosed, with temperature sensor"
        : IsEnclosed ? "Enclosed" : "Open frame";

    public override bool Equals(object? obj) => obj is PrinterModel other && other.Code == Code;
    public override int GetHashCode() => Code.GetHashCode();

    private static PrinterModel M(string code, string name, PrinterShape shape, string prefix, bool enclosed, bool sensor,
        int? heater, bool aux, bool chamberFan, bool humidity, bool lite, int nozzles, int nozzleMax, int bedMax,
        (int, int, int) volume, bool lidar, bool laser) => new()
    {
        Code = code, Name = name, Shape = shape, SerialPrefix = prefix, IsEnclosed = enclosed, HasChamberSensor = sensor,
        ChamberHeaterMax = heater, HasAuxFan = aux, HasChamberFan = chamberFan, HasAmsHumidity = humidity, UsesAmsLite = lite,
        NozzleCount = nozzles, NozzleMax = nozzleMax, BedMax = bedMax, BuildVolume = volume, HasLidar = lidar, HasLaserModule = laser,
    };

    public static readonly IReadOnlyList<PrinterModel> All =
    [
        M("N7", "P2S", PrinterShape.Enclosed, "22E", true, true, null, true, true, true, false, 1, 300, 110, (256, 256, 256), false, false),
        M("C12", "P1S", PrinterShape.Enclosed, "01P", true, false, null, true, true, true, false, 1, 300, 100, (256, 256, 250), false, false),
        M("C11", "P1P", PrinterShape.OpenFrame, "01S", false, false, null, true, true, true, false, 1, 300, 100, (256, 256, 250), false, false),
        M("BL-P001", "X1 Carbon", PrinterShape.Enclosed, "00M", true, true, null, true, true, true, false, 1, 300, 110, (256, 256, 250), true, false),
        M("BL-P002", "X1", PrinterShape.Enclosed, "00W", true, true, null, true, true, true, false, 1, 300, 110, (256, 256, 250), true, false),
        M("C13", "X1E", PrinterShape.Enclosed, "03W", true, true, 60, true, true, true, false, 1, 320, 110, (256, 256, 250), true, false),
        M("N6", "X2D", PrinterShape.Enclosed, "20P", true, true, 65, true, true, true, false, 2, 300, 120, (256, 256, 261), false, false),
        M("N2S", "A1", PrinterShape.BedSlinger, "039", false, false, null, false, false, false, true, 1, 300, 100, (256, 256, 256), false, false),
        M("N1", "A1 mini", PrinterShape.Mini, "030", false, false, null, false, false, false, true, 1, 300, 80, (180, 180, 180), false, false),
        M("N9", "A2L", PrinterShape.BedSlinger, "26A", false, false, null, false, false, false, false, 1, 300, 80, (330, 320, 325), false, false),
        M("O1D", "H2D", PrinterShape.Wide, "094", true, true, 65, true, true, true, false, 2, 350, 120, (350, 320, 325), false, true),
        M("O1E", "H2D Pro", PrinterShape.Wide, "239", true, true, 65, true, true, true, false, 2, 350, 120, (350, 320, 325), false, true),
        M("O1C", "H2C", PrinterShape.Wide, "31B", true, true, 65, true, true, true, false, 2, 350, 120, (330, 320, 325), false, true),
        M("O1S", "H2S", PrinterShape.Wide, "093", true, true, 65, true, true, true, false, 1, 350, 120, (340, 320, 340), false, true),
    ];

    /// <summary>Other ids the same models report (older firmware, Pro and V2 variants).</summary>
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["BL-P003"] = "C13", ["A12"] = "N1", ["A04"] = "N1", ["A11"] = "N2S", ["O2D"] = "O1E", ["O1C2"] = "O1C",
    };

    /// <summary>By model id, then by serial prefix.</summary>
    public static PrinterModel? Find(string code, string serial = "")
    {
        var upper = code.ToUpperInvariant();
        var id = Aliases.TryGetValue(upper, out var alias) ? alias : upper;
        if (All.FirstOrDefault(m => m.Code == id) is { } match) return match;
        var prefix = serial.Length >= 3 ? serial[..3].ToUpperInvariant() : serial.ToUpperInvariant();
        return prefix.Length == 0 ? null : All.FirstOrDefault(m => m.SerialPrefix == prefix);
    }

    /// <summary>By display name ("P2S", "A1 mini") or model id; what a widget gets in its snapshot.</summary>
    public static PrinterModel? Named(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Find(name);
    }

    public static string NameFor(string code, string serial = "") => Find(code, serial)?.Name ?? "Bambu Lab printer";
}

public static class PrinterStatusFitting
{
    /// <summary>Drops readings the model has no sensor or fan for, on <paramref name="status"/> itself (pass a copy). Some firmware reports placeholders there (a P1S sends
    /// a chamber temperature without a sensor, an A1 sends fan fields it doesn't have).</summary>
    public static void Fit(this PrinterStatus status, PrinterModel? model)
    {
        if (model is null) return;
        if (!model.HasChamberSensor) { status.Chamber = null; status.ChamberTarget = null; }
        if (model.ChamberHeaterMax is null) status.ChamberTarget = null;
        if (!model.HasAuxFan) status.AuxFan = null;
        if (!model.HasChamberFan) status.ExhaustFan = null;
        if (!model.IsEnclosed) status.DoorOpen = null;
        if (!model.HasChamberLight) status.ChamberLightOn = null;
        if (model.NozzleCount < 2) status.SecondNozzle = null;
        if (!model.HasAmsHumidity && status.Ams.Any(u => u.Kind == AmsKind.Lite))
        {
            // New units rather than changed ones: the status may share them with an earlier copy.
            status.Ams = status.Ams.Select(u => u.Kind == AmsKind.Lite ? u with { Humidity = null, HumidityLevel = null } : u).ToValueList();
        }
    }
}
