using System.Text.Json;
using BambuBar.Models;

namespace BambuBar.Services;

/// <summary>
/// The last few prints of every printer, newest first, in <c>%APPDATA%\Bambu Bar\history.json</c>. Only what the app saw
/// while running; it backs "Last: …", finish times and end-of-print notifications, not a print log.
/// </summary>
public sealed class HistoryStore
{
    private readonly string? _path;

    /// <param name="path">The file; null keeps nothing (demo and developer runs never touch the real history).</param>
    public HistoryStore(string? path) => _path = path;

    public static HistoryStore ForApp(bool ephemeral) =>
        new(ephemeral ? null : Path.Combine(AppPaths.DataDirectory, "history.json"));

    public List<JobRecord> Load()
    {
        if (_path is null || !File.Exists(_path)) return [];
        try { return JsonSerializer.Deserialize<List<JobRecord>>(File.ReadAllText(_path), JsonOptions.Default) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    public void Save(IEnumerable<JobRecord> jobs)
    {
        if (_path is null) return;
        // A handful per printer is all the UI uses.
        var kept = new List<JobRecord>();
        var perPrinter = new Dictionary<string, int>();
        foreach (var job in jobs)
        {
            var key = job.PrinterSerial ?? "";
            var count = perPrinter.GetValueOrDefault(key);
            if (count >= 5) continue;
            perPrinter[key] = count + 1;
            kept.Add(job);
        }
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(kept, JsonOptions.Indented));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
