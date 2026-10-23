using System.Reflection;
using System.Text.Json;

namespace BambuBar.Protocol;

internal static class EmbeddedResources
{
    public static Stream? Open(string name) => typeof(EmbeddedResources).Assembly.GetManifestResourceStream(name);

    public static IEnumerable<string> Names(string prefix) =>
        typeof(EmbeddedResources).Assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal));
}

/// <summary>English texts for HMS codes and print errors, P2S wording where it differs.
/// Generated from ha-bambulab's catalogue (MIT) by scripts/make-hms.py.</summary>
public sealed class HmsCatalog
{
    public static readonly HmsCatalog Shared = new();

    private Dictionary<string, string> _hms = [];
    private Dictionary<string, string> _errors = [];
    private readonly Lazy<bool> _loaded;

    private HmsCatalog() => _loaded = new Lazy<bool>(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public string? HmsText(string key) => _loaded.Value && _hms.TryGetValue(key.ToUpperInvariant(), out var text) ? text : null;

    public string? ErrorText(string key) => _loaded.Value && _errors.TryGetValue(key.ToUpperInvariant(), out var text) ? text : null;

    public static string WikiUrl(string code) => "https://wiki.bambulab.com/en/hms/home";

    private bool Load()
    {
        using var stream = EmbeddedResources.Open("hms_en.json");
        if (stream is null) return true;
        var json = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream);
        if (json is null) return true;
        _hms = json.GetValueOrDefault("hms") ?? [];
        _errors = json.GetValueOrDefault("error") ?? [];
        return true;
    }
}

/// <summary>Bambu Studio's filament presets and Bambu Lab colour names, so a slot reads "SUNLU PLA Matte" or "Grass Green"
/// rather than a preset id or a hex code. Generated from Bambu Studio's profiles by scripts/make-filaments.py.</summary>
public static class FilamentCatalog
{
    private sealed class Tables
    {
        public Dictionary<string, string[]> Presets { get; set; } = [];
        public Dictionary<string, string> Colors { get; set; } = [];
    }

    private static readonly Lazy<Tables> Data = new(() =>
    {
        using var stream = EmbeddedResources.Open("filaments.json");
        if (stream is null) return new Tables();
        return JsonSerializer.Deserialize<Tables>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Tables();
    });

    /// <summary>The maker and product of a preset id (<c>tray_info_idx</c>): ("SUNLU", "PLA Matte"). Null for user presets.</summary>
    public static (string Vendor, string Product)? Preset(string id) =>
        Data.Value.Presets.TryGetValue(id.ToUpperInvariant(), out var entry) && entry.Length == 2 ? (entry[0], entry[1]) : null;

    /// <summary>Bambu Lab's name for a spool's colour: from the tag's colour code ("A01-G1" → "Grass Green"), else from
    /// the preset and the exact colour a Bambu colour has (picked from the Bambu list in Bambu Studio).</summary>
    public static string? ColorName(string tagCode, string? preset, string hex)
    {
        var parts = tagCode.ToUpperInvariant().Split('-');
        if (parts.Length == 2)
        {
            var letters = new string(parts[1].TakeWhile(char.IsLetter).ToArray());
            var rest = parts[1][letters.Length..];
            var number = int.TryParse(rest, out var n) ? n.ToString() : "";
            if (Data.Value.Colors.TryGetValue($"GF{parts[0]}-{letters}{number}", out var name)) return name;
        }
        if (preset is null || hex.Length < 6) return null;
        return Data.Value.Colors.TryGetValue($"{preset.ToUpperInvariant()}-{hex[..6].ToUpperInvariant()}", out var byColor) ? byColor : null;
    }
}
