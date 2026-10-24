using System.Text.Json.Serialization;

namespace BambuBar.Models;

/// <summary>Which network protocol a printer uses.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrinterProtocol>))]
public enum PrinterProtocol
{
    BambuLan,
    /// <summary>Klipper / Moonraker / Fluidd / Mainsail (including Snapmaker U1 with paxx12 firmware).</summary>
    Moonraker,
}

/// <summary>Where to find the printer. The access code lives in Windows Credential Manager, not here.</summary>
public sealed record PrinterConfig
{
    public string Host { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>SSDP model code ("N7" for the P2S, "SM-U1" for Snapmaker U1).</summary>
    public string ModelCode { get; set; } = "";
    public PrinterProtocol Protocol { get; set; } = PrinterProtocol.BambuLan;

    public string ModelName => Protocol == PrinterProtocol.Moonraker && string.IsNullOrEmpty(ModelCode)
        ? "Snapmaker U1"
        : PrinterModel.NameFor(ModelCode, Serial);

    public string FullModelName => Protocol == PrinterProtocol.Moonraker && string.IsNullOrEmpty(ModelCode)
        ? "Snapmaker U1"
        : PrinterModel.FullNameFor(ModelCode, Serial);

    public PrinterModel? Model => Protocol == PrinterProtocol.Moonraker && string.IsNullOrEmpty(ModelCode)
        ? PrinterModel.Find("SM-U1")
        : PrinterModel.Find(ModelCode, Serial);

    /// <summary>Bambu printers listen on 8883; Moonraker/Fluidd defaults to 80 (or 7125); <c>host:port</c> overrides.</summary>
    public (string Host, int Port) Endpoint
    {
        get
        {
            var clean = Host.Trim();
            if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) clean = clean[7..];
            else if (clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) clean = clean[8..];
            clean = clean.TrimEnd('/');
            var parts = clean.Split(':');
            var defaultPort = Protocol == PrinterProtocol.Moonraker ? 80 : 8883;
            return parts.Length == 2 && int.TryParse(parts[1], out var port) && port is > 0 and < 65536
                ? (parts[0], port)
                : (clean, defaultPort);
        }
    }
}

/// <summary>What a widget shows about one printer.</summary>
public sealed record SharedSnapshot
{
    public enum LinkState
    {
        /// <summary>No printer set up yet.</summary>
        Unconfigured,
        Connecting,
        Online,
        /// <summary>Configured, but the printer isn't answering.</summary>
        Offline,
        Unauthorized,
    }

    public string Serial { get; set; } = "";
    public string PrinterName { get; set; } = "";
    public string Model { get; set; } = "";
    public LinkState Link { get; set; }
    public PrinterStatus? Status { get; set; }
    public JobRecord? LastJob { get; set; }
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;

    public static SharedSnapshot Placeholder => new()
    {
        PrinterName = "P2S", Model = "P2S", Link = LinkState.Online, Status = SamplePrinter.Status(), SavedAt = DateTime.UtcNow,
    };

    public static SharedSnapshot Unconfigured(DateTime? now = null) => new()
    {
        PrinterName = "Bambu Bar", Model = "P2S", Link = LinkState.Unconfigured, SavedAt = now ?? DateTime.UtcNow,
    };
}

/// <summary>Every printer, as the widgets see them.</summary>
public sealed record SharedFleet(IReadOnlyList<SharedSnapshot> Printers, string? MenuBarSerial, DateTime SavedAt)
{
    /// <summary>The snapshot for a widget: its chosen printer, else the tray's, else the first.</summary>
    public SharedSnapshot SnapshotFor(string? serial)
    {
        if (serial is not null && Printers.FirstOrDefault(p => p.Serial == serial) is { } match) return match;
        if (MenuBarSerial is not null && Printers.FirstOrDefault(p => p.Serial == MenuBarSerial) is { } tray) return tray;
        return Printers.FirstOrDefault() ?? SharedSnapshot.Unconfigured(SavedAt);
    }
}

public static class SamplePrinter
{
    /// <summary>A mid-print P2S with an AMS 2 Pro, for previews, placeholders and the demo.</summary>
    public static PrinterStatus Status() => new()
    {
        State = JobState.Printing,
        StageId = 0,
        JobName = "Rear Dry Pod",
        Progress = 64,
        RemainingMinutes = 62,
        Layer = 170,
        TotalLayers = 305,
        Nozzle = new(220, 220),
        Bed = new(55, 55),
        Chamber = 38,
        PartFan = 90,
        AuxFan = 40,
        Speed = SpeedLevel.Standard,
        ChamberLightOn = true,
        DoorOpen = false,
        WifiSignal = -52,
        Ams =
        [
            new AmsUnit
            {
                Id = 0, Kind = AmsKind.Pro2, Humidity = 16, HumidityLevel = 1, Temperature = 28.4, DryingMinutesLeft = 0,
                Spools =
                [
                    new Spool { Id = 0, Unit = 0, Slot = 0, Material = "PLA", Name = "PLA Matte", Color = "E8DBB7FF", Colors = ["E8DBB7FF"], Remain = 81, Weight = 1000,
                                IsLoaded = true, IsBambu = true, Preset = "GFA01", Brand = "Bambu Lab", ColorName = "Desert Tan", NozzleRange = new(190, 230) },
                    new Spool { Id = 1, Unit = 0, Slot = 1, Material = "PLA", Name = "PLA Basic", Color = "000000FF", Colors = ["000000FF"], Remain = 39, Weight = 1000,
                                IsLoaded = true, IsBambu = true, Preset = "GFA00", Brand = "Bambu Lab", ColorName = "Black", NozzleRange = new(190, 230) },
                    new Spool { Id = 2, Unit = 0, Slot = 2, Material = "PLA", Name = "PLA+", Color = "3F8E43FF", Colors = ["3F8E43FF"], Remain = null,
                                IsLoaded = true, IsBambu = false, Preset = "GFSNL03", Brand = "SUNLU", NozzleRange = new(200, 230) },
                    new Spool { Id = 3, Unit = 0, Slot = 3, Material = "PLA", Name = "PLA Silk+", Color = "F4A925FF", Colors = ["F4A925FF"], Remain = 22, Weight = 1000,
                                IsLoaded = true, IsBambu = true, Preset = "GFA06", Brand = "Bambu Lab", ColorName = "Gold", NozzleRange = new(200, 240) },
                ],
            },
        ],
        ActiveSpoolId = 3,
        ExternalSpool = new Spool
        {
            Id = Spool.ExternalId, Unit = 255, Slot = 0, Material = "PETG", Name = "PETG HF", Color = "1F79E5FF", Colors = ["1F79E5FF"],
            Remain = null, Weight = 1000, IsLoaded = true, IsBambu = true, Preset = "GFG02", Brand = "Bambu Lab", ColorName = "Lake Blue",
            NozzleRange = new(230, 270),
        },
        Firmware = "01.03.00.00",
        NozzleDiameter = 0.4,
        NozzleType = "HS01",
        HasSdCard = true,
    };
}
