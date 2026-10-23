using System.Globalization;
using System.Text.Json.Nodes;
using BambuBar.Models;

namespace BambuBar.Protocol;

/// <summary>
/// The printer sends one full <c>print</c> report after <c>pushall</c>, then only the fields that changed.
/// <see cref="ReportDocument"/> keeps the merged picture.
/// </summary>
public sealed class ReportDocument
{
    public JsonObject Print { get; private set; } = new();
    /// <summary>From <c>get_version</c>: module name → (product name, software version).</summary>
    public Dictionary<string, (string Product, string Version)> Modules { get; } = [];
    public bool HasFullReport { get; private set; }

    /// <summary>Merges one message from <c>device/&lt;serial&gt;/report</c>. Returns true if the print state changed.</summary>
    public bool Apply(JsonObject message)
    {
        var changed = false;
        if (message["print"] is JsonObject p)
        {
            var command = J.String(p["command"]);
            // Command acknowledgements echo the request back; only status pushes describe the printer.
            if (command is null || command == "push_status")
            {
                if (J.Long(p["msg"]) == 0 && p.ContainsKey("gcode_state") && p["msg"] is not null)
                {
                    // A full report replaces everything, so unplugged AMS units and cleared errors go away.
                    HasFullReport = true;
                    Print = (JsonObject)p.DeepClone();
                }
                else
                {
                    Merge(Print, p);
                }
                changed = true;
            }
        }
        if (message["info"] is JsonObject info && J.String(info["command"]) == "get_version" && info["module"] is JsonArray list)
        {
            foreach (var node in list)
            {
                if (node is not JsonObject m || J.String(m["name"]) is not { } name) continue;
                Modules[name] = (J.String(m["product_name"]) ?? "", J.String(m["sw_ver"]) ?? "");
            }
            changed = true;
        }
        return changed;
    }

    public void Reset()
    {
        Print = new JsonObject();
        Modules.Clear();
        HasFullReport = false;
    }

    /// <summary>Recursive merge into <paramref name="target"/>. Arrays of objects carrying an <c>id</c> (or, for
    /// <c>lights_report</c>, a <c>node</c>) merge entry by entry; other arrays are replaced.</summary>
    public static void Merge(JsonObject target, JsonObject delta)
    {
        foreach (var (key, value) in delta)
        {
            var idKey = key == "lights_report" ? "node" : "id";
            var old = target[key];
            if (value is JsonObject newDict && old is JsonObject oldDict)
            {
                Merge(oldDict, newDict);
            }
            else if (value is JsonArray newArray && old is JsonArray oldArray && newArray.Count > 0
                     && newArray.All(i => i is JsonObject o && o[idKey] is not null))
            {
                foreach (var node in newArray)
                {
                    var item = (JsonObject)node!;
                    var id = J.Describe(item[idKey]);
                    var match = oldArray.OfType<JsonObject>().FirstOrDefault(o => J.Describe(o[idKey]) == id);
                    if (match is not null) Merge(match, item);
                    else oldArray.Add(item.DeepClone());
                }
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }
}

/// <summary>Turns the merged report into a <see cref="PrinterStatus"/>.</summary>
public static class ReportParser
{
    public static PrinterStatus Status(ReportDocument doc, HmsCatalog hms)
    {
        var p = doc.Print;
        var s = new PrinterStatus { UpdatedAt = DateTime.UtcNow };
        s.State = JobStateExtensions.FromGcodeState(J.String(p["gcode_state"]) ?? "IDLE");
        s.StageId = J.Int(p["stg_cur"]) ?? J.Int((p["stage"] as JsonObject)?["_id"]);
        if (s.StageId == 0 && J.String(p["print_type"]) == "idle" && !s.State.IsActive()) s.StageId = 255;
        s.TaskId = new[] { J.String(p["task_id"]), J.String(p["subtask_id"]) }.FirstOrDefault(t => !string.IsNullOrEmpty(t) && t != "0");
        s.JobName = CleanJobName(J.String(p["subtask_name"]) ?? "", J.String(p["gcode_file"]) ?? "");
        s.Progress = Math.Clamp(J.Int(p["mc_percent"]) ?? 0, 0, 100);
        s.RemainingMinutes = Math.Max(0, J.Int(p["mc_remaining_time"]) ?? 0);
        s.Layer = J.Int(p["layer_num"]) ?? 0;
        s.TotalLayers = J.Int(p["total_layer_num"]) ?? 0;

        // Temperatures: the newer `device` block packs current (low 16 bits) and target (high 16 bits).
        var device = p["device"] as JsonObject;
        var extruders = (device?["extruder"] as JsonObject)?["info"] as JsonArray;
        if (J.Long((extruders?.FirstOrDefault() as JsonObject)?["temp"]) is long packed)
        {
            s.Nozzle = Unpack(packed);
            if (extruders!.Count > 1 && J.Long((extruders[1] as JsonObject)?["temp"]) is long second) s.SecondNozzle = Unpack(second);
        }
        else
        {
            s.Nozzle = new Temperature(J.Double(p["nozzle_temper"]) ?? 0, J.Double(p["nozzle_target_temper"]) ?? 0);
        }
        if ((J.Long(((device?["bed"] as JsonObject)?["info"] as JsonObject)?["temp"]) ?? J.Long(device?["bed_temp"])) is long bed)
            s.Bed = Unpack(bed);
        else
            s.Bed = new Temperature(J.Double(p["bed_temper"]) ?? 0, J.Double(p["bed_target_temper"]) ?? 0);
        if (J.Long(((device?["ctc"] as JsonObject)?["info"] as JsonObject)?["temp"]) is long ctc)
        {
            var chamber = Unpack(ctc);
            s.Chamber = chamber.Current;
            s.ChamberTarget = chamber.Target;
        }
        else if ((J.Double((p["info"] as JsonObject)?["temp"]) ?? J.Double(p["chamber_temper"])) is double t)
        {
            s.Chamber = t;
        }
        if (s.Chamber is double c && (c <= 0 || c > 120)) { s.Chamber = null; s.ChamberTarget = null; }

        // Fans: the `airduct` parts already report percent; the legacy fields are 0-15.
        if ((device?["airduct"] as JsonObject)?["parts"] is JsonArray parts && parts.Count > 0)
        {
            foreach (var node in parts)
            {
                if (node is not JsonObject part || J.Long(part["id"]) is not long id || J.Long(part["state"]) is not long state) continue;
                var pct = (int)Math.Clamp(state & 0xFF, 0, 100);
                switch (id >> 4)
                {
                    case 1: s.PartFan = pct; break;
                    case 2: s.AuxFan = pct; break;
                    case 3: s.ExhaustFan = pct; break;
                }
            }
        }
        else
        {
            s.PartFan = Fan(p["cooling_fan_speed"]);
            s.AuxFan = Fan(p["big_fan1_speed"]);
            if (Fan(p["big_fan2_speed"]) is int v && v > 0) s.ExhaustFan = v;
        }
        s.PartFan ??= Fan(p["cooling_fan_speed"]);
        s.HeatbreakFan = Fan(p["heatbreak_fan_speed"]);

        var speed = J.Int(p["spd_lvl"]) ?? 2;
        s.Speed = Enum.IsDefined(typeof(SpeedLevel), speed) ? (SpeedLevel)speed : SpeedLevel.Standard;
        if (p["lights_report"] is JsonArray lights
            && lights.OfType<JsonObject>().FirstOrDefault(l => J.String(l["node"]) == "chamber_light") is { } light)
            s.ChamberLightOn = J.String(light["mode"]) != "off";
        if (J.String(p["stat"]) is { } stat && ulong.TryParse(stat, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var statBits))
            s.DoorOpen = (statBits & 0x0080_0000) != 0;
        if (J.String(p["wifi_signal"]) is { } wifi && int.TryParse(wifi.Replace("dBm", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dbm))
            s.WifiSignal = dbm;
        if (J.String(p["fun"]) is { } fun && ulong.TryParse(fun, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var funBits))
            s.ControlsLocked = (funBits & 0x2000_0000) != 0;
        s.HasSdCard = J.Bool(p["sdcard"]);
        s.PlateIndex = J.Int(p["plate_idx"]);
        if (p["ipcam"] is JsonObject ipcam && J.String(ipcam["rtsp_url"]) is { } url)
            s.LiveViewEnabled = url != "disable" && url.Length > 0;
        if ((device?["nozzle"] as JsonObject)?["info"] is JsonArray nozzles && nozzles.FirstOrDefault() is JsonObject n)
        {
            s.NozzleDiameter = J.Double(n["diameter"]);
            s.NozzleType = J.String(n["type"]);
        }
        else
        {
            s.NozzleDiameter = J.Double(p["nozzle_diameter"]);
            s.NozzleType = J.String(p["nozzle_type"]);
        }
        if (p["upgrade_state"] is JsonObject upgrade && J.Long(upgrade["new_version_state"]) == 1) s.UpdateAvailable = true;
        s.Firmware = doc.Modules.TryGetValue("ota", out var ota) ? ota.Version : null;

        ParseFilament(p, device, doc, s);
        s.Issues = Issues(p, hms);
        s.Canceled = s.State == JobState.Failed && WasCanceled(doc);
        return s;
    }

    // MARK: - Filament

    private static void ParseFilament(JsonObject p, JsonObject? device, ReportDocument doc, PrinterStatus s)
    {
        var amsRoot = p["ams"] as JsonObject;
        var units = (amsRoot?["ams"] as JsonArray)?.OfType<JsonObject>() ?? [];
        var existBits = Hex(J.String(amsRoot?["tray_exist_bits"]));
        var rfidBits = Hex(J.String(amsRoot?["tray_is_bbl_bits"]));
        var parsedUnits = new List<AmsUnit>();
        foreach (var u in units)
        {
            if (J.Int(u["id"]) is not int id) continue;
            var kind = AmsKindOf(id, J.String(u["info"]), doc.Modules);
            var spools = new List<Spool>();
            foreach (var t in (u["tray"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                if (J.Int(t["id"]) is not int slot) continue;
                var global = id >= 128 ? id : id * 4 + slot;
                var bit = id >= 128 ? 16 + (id - 128) : id * 4 + slot;
                var spool = SpoolFrom(t, global, id, slot);
                if (existBits is ulong exist && bit is >= 0 and < 64 && (exist & (1UL << bit)) == 0) spool.IsLoaded = false;
                // Only ever narrows the tag check: the P2S sets every bit, Bambu spool or not.
                if (rfidBits is ulong rfid && bit is >= 0 and < 64 && (rfid & (1UL << bit)) == 0) spool.IsBambu = false;
                spool = Describe(spool);
                spools.Add(spool);
            }
            int? humidity = J.Int(u["humidity_raw"]);
            if (humidity is int h && (h < 1 || h > 100)) humidity = null;
            int? level = J.Int(u["humidity"]);
            if (level is int l && (l < 1 || l > 5)) level = null;
            double? temp = J.Double(u["temp"]);
            if (temp is double tt && (tt <= 0 || tt > 100)) temp = null;
            parsedUnits.Add(new AmsUnit
            {
                Id = id, Kind = kind, Humidity = humidity, HumidityLevel = level.HasValue ? 6 - level.Value : null, Temperature = temp,
                DryingMinutesLeft = J.Int(u["dry_time"]) ?? 0, Spools = spools.OrderBy(x => x.Slot).ToValueList(),
            });
        }
        s.Ams = parsedUnits.OrderBy(x => x.Id).ToValueList();

        var virtualSlot = ((p["vir_slot"] as JsonArray)?.FirstOrDefault() as JsonObject) ?? (p["vt_tray"] as JsonObject);
        if (virtualSlot is not null)
        {
            var ext = SpoolFrom(virtualSlot, Spool.ExternalId, 255, 0);
            ext.IsLoaded = !string.IsNullOrEmpty(J.String(virtualSlot["tray_type"]));
            // Nothing weighs the spool on the external holder; its `remain` is a placeholder.
            ext.Remain = null;
            s.ExternalSpool = Describe(ext);
        }

        // Active slot: the extruder's `snow` (unit << 8 | slot) on newer firmware, else `tray_now`.
        var extruders = (device?["extruder"] as JsonObject)?["info"] as JsonArray;
        if (J.Int((extruders?.FirstOrDefault() as JsonObject)?["snow"]) is int snow && snow != 0xFFFF)
        {
            int unit = snow >> 8, slot = snow & 0xFF;
            if (unit is 254 or 255) s.ActiveSpoolId = slot == 255 ? null : Spool.ExternalId;
            else if (unit >= 128) s.ActiveSpoolId = unit;
            else s.ActiveSpoolId = unit * 4 + (slot & 0x3);
        }
        else if (J.Int(amsRoot?["tray_now"]) is int now)
        {
            s.ActiveSpoolId = now switch { 255 => null, 254 => Spool.ExternalId, _ => now };
        }
        // Only meaningful while filament is actually in use.
        if (!s.State.IsActive() && s.ActiveSpoolId is not null && s.ActiveSpool?.IsLoaded != true) s.ActiveSpoolId = null;
    }

    private static Spool SpoolFrom(JsonObject t, int id, int unit, int slot)
    {
        var material = J.String(t["tray_type"]) ?? "";
        var state = J.Int(t["state"]);
        var loaded = material.Length > 0;
        if (state is int st && st > 3) loaded = ((st & 0x01) != 0 && (st & 0x08) != 0) || st == 9;
        var color = J.String(t["tray_color"]) ?? "00000000";
        var colors = (t["cols"] as JsonArray)?.Select(J.String).OfType<string>().Where(c => c.Length >= 6).ToList() ?? [];
        int? remain = J.Int(t["remain"]);
        if (remain is int r && (r < 0 || r > 100)) remain = null;
        var tag = J.String(t["tag_uid"]) ?? "";
        int? weight = J.Int(t["tray_weight"]);
        if (weight is int w && (w <= 0 || w > 10_000)) weight = null;
        var preset = J.String(t["tray_info_idx"]);
        var spool = new Spool
        {
            Id = id, Unit = unit, Slot = slot, Material = material, Name = J.String(t["tray_sub_brands"]) ?? "",
            Color = color, Colors = (colors.Count == 0 ? new List<string> { color } : colors).ToValueList(), Remain = remain, Weight = weight,
            IsLoaded = loaded, IsBambu = tag.Length > 0 && !tag.All(ch => ch == '0'),
            Preset = string.IsNullOrEmpty(preset) ? null : preset,
        };
        spool.ColorName = FilamentCatalog.ColorName(J.String(t["tray_id_name"]) ?? "", spool.Preset, color);
        if (J.Int(t["nozzle_temp_min"]) is int low && J.Int(t["nozzle_temp_max"]) is int high && low > 0 && high >= low)
            spool.NozzleRange = new IntRange(low, high);
        return spool;
    }

    /// <summary>Fills in the maker and product from the slot's preset once it's known whether the spool has a Bambu tag.
    /// A spool without one is whatever the slot was set to on the printer or in Bambu Studio.</summary>
    private static Spool Describe(Spool spool)
    {
        if (!spool.IsLoaded)
        {
            return new Spool
            {
                Id = spool.Id, Unit = spool.Unit, Slot = spool.Slot, Material = "", Name = "", Color = spool.Color,
                Colors = spool.Colors, Remain = null, IsLoaded = false, IsBambu = false,
            };
        }
        var preset = spool.Preset is { } id ? FilamentCatalog.Preset(id) : null;
        spool.Brand = spool.IsBambu ? "Bambu Lab" : preset?.Vendor;
        if (spool.Name.Length == 0 && preset is { } pr && !string.Equals(pr.Product, spool.Material, StringComparison.OrdinalIgnoreCase))
            spool.Name = pr.Product;
        return spool;
    }

    private static AmsKind AmsKindOf(int id, string? info, Dictionary<string, (string Product, string Version)> modules)
    {
        if (id >= 128 || modules.ContainsKey($"n3s/{id}")) return AmsKind.Ht;
        if (modules.ContainsKey($"n3f/{id}")) return AmsKind.Pro2;
        if (modules.ContainsKey($"ams_f1/{id}")) return AmsKind.Lite;
        if (modules.ContainsKey($"ams/{id}")) return AmsKind.Ams;
        if (Hex(info) is ulong bits)
        {
            return (bits & 0xF) switch
            {
                2 => AmsKind.Lite,
                3 => AmsKind.Pro2,
                4 => AmsKind.Ht,
                _ => AmsKind.Ams,
            };
        }
        return AmsKind.Ams;
    }

    // MARK: - Issues

    private static ValueList<PrinterIssue> Issues(JsonObject p, HmsCatalog catalog)
    {
        var output = new List<PrinterIssue>();
        foreach (var entry in (p["hms"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (J.Long(entry["attr"]) is not long attr || J.Long(entry["code"]) is not long code) continue;
            uint a = unchecked((uint)attr), c = unchecked((uint)code);
            if ((c & 0xFFFF) < 0x4000 && c >> 16 == 0) continue;
            var key = $"{a:X8}{c:X8}";
            var pretty = $"HMS_{a >> 16:X4}_{a & 0xFFFF:X4}_{c >> 16:X4}_{c & 0xFFFF:X4}";
            var level = (int)(c >> 16);
            var severity = Enum.IsDefined(typeof(IssueSeverity), level) ? (IssueSeverity)level : IssueSeverity.Common;
            output.Add(new PrinterIssue(pretty, severity, catalog.HmsText(key) ?? $"The printer reported {pretty}.", HmsCatalog.WikiUrl(pretty)));
        }
        if (J.Long(p["print_error"]) is long error && error != 0)
        {
            var e = unchecked((uint)error);
            var key = $"{e:X8}";
            // 0300_400C and 0500_400E are the printer's echo of a user cancel, not a fault.
            if (key != "0300400C" && key != "0500400E")
            {
                var pretty = $"{e >> 16:X4}_{e & 0xFFFF:X4}";
                var level = (e & 0xF000) >> 12;
                var severity = level == 0x4 ? IssueSeverity.Fatal : level == 0x8 ? IssueSeverity.Serious : IssueSeverity.Common;
                if (!output.Any(i => i.Id.EndsWith(pretty, StringComparison.Ordinal)))
                    output.Add(new PrinterIssue(pretty, severity, catalog.ErrorText(key) ?? $"The printer reported error {pretty}.", HmsCatalog.WikiUrl(pretty)));
            }
        }
        // Stable, like Swift's sort on this short list: equal severities keep the printer's order.
        return output.OrderBy(i => (int)i.Severity).ToValueList();
    }

    /// <summary>True when <c>print_error</c> is the code for a job the user cancelled.</summary>
    public static bool WasCanceled(ReportDocument doc)
    {
        if (J.Long(doc.Print["print_error"]) is not long e) return false;
        var key = $"{unchecked((uint)e):X8}";
        return key is "0300400C" or "0500400E";
    }

    // MARK: - Helpers

    /// <summary>Bambu Studio names jobs after the 3MF; strip path and extensions for display.</summary>
    public static string CleanJobName(string name, string file)
    {
        var n = name.Trim();
        if (n.Length == 0)
        {
            n = file.Split('/', '\\').LastOrDefault() ?? "";
            if (n.StartsWith("plate_", StringComparison.Ordinal)) n = "";
        }
        foreach (var ext in new[] { ".gcode.3mf", ".3mf", ".gcode" })
        {
            if (n.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) n = n[..^ext.Length];
        }
        // Bambu Studio turns spaces into underscores when it names the job after the project file.
        if (!n.Contains(' ')) n = n.Replace('_', ' ');
        return n;
    }

    /// <summary>Plain values (no target packed in) stay below 0x10000; a heater that's off reports target 0.</summary>
    private static Temperature Unpack(long packed) => new(packed & 0xFFFF, (packed >> 16) & 0xFFFF);

    private static int? Fan(JsonNode? value)
    {
        if (J.Long(value) is not long raw) return null;
        if (raw <= 15) return (int)Math.Round(raw / 15.0 * 100, MidpointRounding.AwayFromZero);
        return (int)Math.Min(100, Math.Round(raw / 255.0 * 100, MidpointRounding.AwayFromZero));
    }

    private static ulong? Hex(string? text) =>
        text is not null && ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>Loose readers for the printer's JSON, which sends the same field as a number on one firmware and a
/// string on another.</summary>
public static class J
{
    public static long? Long(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return double.IsFinite(d) ? (long)d : null;
        if (v.TryGetValue<bool>(out var b)) return b ? 1 : 0;
        if (v.TryGetValue<string>(out var s))
        {
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ls)) return ls;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var ds) && double.IsFinite(ds)) return (long)ds;
        }
        return null;
    }

    public static int? Int(JsonNode? node) => Long(node) is long l ? (int)Math.Clamp(l, int.MinValue, int.MaxValue) : null;

    public static double? Double(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var ds)) return ds;
        return null;
    }

    public static string? String(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s)) return s;
        if (v.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
        if (v.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
        if (v.TryGetValue<bool>(out var b)) return b ? "1" : "0";
        return null;
    }

    public static bool? Bool(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<long>(out var l)) return l != 0;
        return null;
    }

    /// <summary>A value as text for comparing ids, whatever its JSON type.</summary>
    public static string Describe(JsonNode? node) => node is null ? "" : String(node) ?? node.ToJsonString();
}
