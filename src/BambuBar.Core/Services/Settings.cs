using System.Text.Json;
using System.Text.Json.Nodes;

namespace BambuBar.Services;

/// <summary>
/// Preferences and the printer list, like macOS user defaults: one JSON file in <see cref="AppPaths.DataDirectory"/>,
/// written atomically after each change. An ephemeral store (demo and developer runs) never touches the disk.
/// </summary>
public sealed class SettingsStore
{
    private readonly string? _path;
    private readonly JsonObject _values;
    private readonly object _lock = new();

    public static SettingsStore Shared { get; set; } = new(null);

    /// <param name="path">The file; null keeps everything in memory.</param>
    public SettingsStore(string? path)
    {
        _path = path;
        _values = Load(path);
    }

    public static SettingsStore ForApp(bool ephemeral) =>
        ephemeral ? new SettingsStore(null) : new SettingsStore(Path.Combine(AppPaths.DataDirectory, "settings.json"));

    private static JsonObject Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new JsonObject();
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new JsonObject(); }
    }

    public bool Contains(string key)
    {
        lock (_lock) return _values.ContainsKey(key);
    }

    public string? GetString(string key)
    {
        lock (_lock) return _values[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    public bool? GetBool(string key)
    {
        lock (_lock) return _values[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    }

    public double? GetDouble(string key)
    {
        lock (_lock) return _values[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    }

    public DateTime? GetDate(string key) =>
        GetString(key) is { } s && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : null;

    public T? Get<T>(string key)
    {
        JsonNode? node;
        lock (_lock) node = _values[key]?.DeepClone();
        if (node is null) return default;
        try { return node.Deserialize<T>(Models.JsonOptions.Default); }
        catch (JsonException) { return default; }
    }

    public void Set(string key, string? value) => SetNode(key, value is null ? null : JsonValue.Create(value));
    public void Set(string key, bool value) => SetNode(key, JsonValue.Create(value));
    public void Set(string key, double value) => SetNode(key, JsonValue.Create(value));
    public void Set(string key, DateTime value) => SetNode(key, JsonValue.Create(value.ToUniversalTime().ToString("O")));
    public void SetObject<T>(string key, T value) => SetNode(key, JsonSerializer.SerializeToNode(value, Models.JsonOptions.Default));

    public void Remove(string key) => SetNode(key, null);

    private void SetNode(string key, JsonNode? node)
    {
        lock (_lock)
        {
            if (node is null) _values.Remove(key);
            else _values[key] = node;
            Save();
        }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, _values.ToJsonString(Models.JsonOptions.Indented));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
