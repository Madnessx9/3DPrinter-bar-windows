using System.Text.Json;
using System.Text.Json.Nodes;
using BambuBar.Models;
using BambuBar.Protocol;

namespace BambuBar.Services;

/// <summary>
/// <c>--probe &lt;host&gt; &lt;serial&gt; &lt;access-code&gt; [seconds]</c>: connects like the app does, prints what happens and a
/// summary of the first full report, then exits. For diagnosing a printer from a terminal without the UI. Add
/// <c>--insecure-tls</c> for the local test broker. Pass <c>saved</c> as the code to use the one Bambu Bar saved, and
/// <c>--raw-filament</c> to print the AMS and external spool fields as the printer sent them.
/// </summary>
public static class Probe
{
    public sealed record Arguments(string Host, string Serial, string Code, double Seconds, bool Insecure = false, bool RawFilament = false);

    /// <summary>The probe's arguments, or null when the app wasn't started with <c>--probe</c>.</summary>
    public static Arguments? Parse(AppArguments args, Func<string, string?> savedCode)
    {
        var all = args.All;
        var i = all.ToList().FindIndex(a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 3 >= all.Count) return null;
        var seconds = i + 4 < all.Count && double.TryParse(all[i + 4], System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 20;
        // "keychain" is what the macOS app takes; both work.
        var code = all[i + 3] is "saved" or "keychain" ? savedCode(all[i + 2]) ?? "" : all[i + 3];
        return new Arguments(all[i + 1], all[i + 2], code, seconds, args.InsecureTls, args.Has("--raw-filament"));
    }

    /// <summary>Runs on the main thread; calls <paramref name="exit"/> with the exit code when done.</summary>
    public static PrinterConnection Start(Arguments a, TextWriter output, Action<int> exit)
    {
        var started = DateTime.UtcNow;
        void Log(string text)
        {
            output.WriteLine($"[{(DateTime.UtcNow - started).TotalSeconds,5:0.0}s] {text}");
            output.Flush();
        }
        var connection = new PrinterConnection();
        var reports = 0;
        var raw = a.RawFilament;
        connection.PhaseChanged += phase =>
        {
            Log($"phase: {phase}");
            if (phase is not ConnectionPhase.Connecting && BambuTrust.LastVerdict is { } verdict) Log($"certificate: {verdict}");
        };
        connection.Acknowledged += (command, ok, reason) => Log($"ack: {command} {(ok ? "ok" : "failed")} {reason ?? ""}");
        connection.StatusChanged += (status, doc) =>
        {
            reports++;
            if (reports == 1 || reports % 10 == 0) Log($"status #{reports}: {Summary(status)}");
            if (reports == 1 && raw)
            {
                var fields = new JsonObject();
                foreach (var key in new[] { "ams", "vt_tray", "vir_slot" }) fields[key] = doc.Print[key]?.DeepClone();
                Log("raw filament:\n" + fields.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        };
        Log($"connecting to {a.Host} as {a.Serial}");
        connection.Start(new PrinterConfig { Host = a.Host, Serial = a.Serial, Name = a.Serial }, a.Code, a.Insecure);
        MainTimer.After(TimeSpan.FromSeconds(a.Seconds), () =>
        {
            Log($"done: {reports} status updates");
            connection.Stop();
            exit(reports > 0 ? 0 : 1);
        });
        return connection;
    }

    public static string Summary(PrinterStatus s)
    {
        string Opt<T>(T? v) where T : struct => v is { } x ? Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture)! : "-";
        var lines = new List<string>
        {
            $"{s.State.Title()}, stage {Opt(s.StageId)} ({s.StageText ?? "-"})",
            $"job \"{s.JobName}\" {s.Progress}% layer {s.Layer}/{s.TotalLayers}, {s.RemainingMinutes} min left",
            $"nozzle {s.Nozzle.Current}/{s.Nozzle.Target} bed {s.Bed.Current}/{s.Bed.Target} chamber {Opt(s.Chamber)}",
            $"fans part {s.PartFan ?? -1}% aux {s.AuxFan ?? -1}% exhaust {s.ExhaustFan ?? -1}%  speed {s.Speed.Title()}",
            $"light {(s.ChamberLightOn is bool l ? l ? "on" : "off" : "-")} door {(s.DoorOpen is bool d ? d ? "open" : "closed" : "-")} wifi {s.WifiSignal ?? 0} dBm",
            $"controls locked: {s.ControlsLocked}  firmware {s.Firmware ?? "-"}  nozzle {s.NozzleDiameter ?? 0} {s.NozzleType ?? "-"}",
        };
        foreach (var unit in s.Ams)
        {
            var spools = unit.Spools.Select(sp => $"{sp.Label} " + (sp.IsLoaded ? $"{sp.Brand ?? "?"} {sp.DisplayName} {sp.ColorName ?? "#" + sp.Color[..Math.Min(6, sp.Color.Length)]} {sp.RemainText}" : "empty"));
            lines.Add($"{unit.Title} {(unit.Humidity is int h ? $"{h}%RH" : "")} {(unit.Temperature is double t ? $"{t}°" : "")}: " + string.Join(", ", spools));
        }
        if (s.ExternalSpool is { } ext)
            lines.Add("external: " + (ext.IsLoaded ? $"{ext.Brand ?? "?"} {ext.DisplayName} {ext.ColorName ?? "#" + ext.Color[..Math.Min(6, ext.Color.Length)]}" : "empty"));
        lines.Add($"active spool: {Opt(s.ActiveSpoolId)}");
        foreach (var issue in s.Issues) lines.Add($"issue {issue.Id} [{issue.Severity.Title()}]: {issue.Message}");
        return string.Join("\n         ", lines);
    }
}
