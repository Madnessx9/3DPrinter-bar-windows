using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BambuBar.Protocol;

/// <summary>A printer some slicer already knows the access code for.</summary>
public sealed record SlicerPrinter
{
    public string Serial { get; init; } = "";
    /// <summary>Best first. Bambu Studio keeps the code it got from the cloud (<c>access_code</c>) and the one typed in
    /// for LAN mode (<c>user_access_code</c>); either may be stale, so both are kept to try in turn.</summary>
    public IReadOnlyList<string> AccessCodes { get; init; } = [];
    public string? Host { get; init; }
    public string? Name { get; init; }
    /// <summary>"Bambu Studio", "OrcaSlicer"…</summary>
    public string Source { get; init; } = "";

    public bool Equals(SlicerPrinter? other) =>
        other is not null && Serial == other.Serial && AccessCodes.SequenceEqual(other.AccessCodes) && Host == other.Host
        && Name == other.Name && Source == other.Source;

    public override int GetHashCode() => HashCode.Combine(Serial, Host, Source);
}

/// <summary>
/// Reads access codes out of Bambu Studio's and OrcaSlicer's settings, so setup needs no typing.
///
/// Both slicers save <c>%APPDATA%\&lt;App&gt;\&lt;App&gt;.conf</c> (on a Mac <c>~/Library/Application Support/&lt;App&gt;/</c>) as JSON,
/// on Windows with an MD5 line after it, with one object per section: <c>access_code</c> and <c>user_access_code</c> map
/// serial → code, <c>ip_address</c> and <c>user_access_dev_ip</c> map serial → IP (BambuStudio <c>DevManager.cpp</c>,
/// <c>AppConfig.cpp</c>). Newer Bambu Studio versions save <c>user_access_dev_ip</c> scrambled with the install's
/// <c>app.slicer_uuid</c> (see <see cref="DecodeIp"/>). Names and models aren't saved; they come from the printer's
/// network announcements.
/// </summary>
public static class SlicerImport
{
    public static readonly (string Folder, string Title)[] KnownApps =
        [("BambuStudio", "Bambu Studio"), ("BambuStudioBeta", "Bambu Studio Beta"), ("OrcaSlicer", "OrcaSlicer"), ("OrcaSlicerNightly", "OrcaSlicer Nightly")];

    /// <summary>This PC's <c>%APPDATA%</c> (Roaming), where both slicers keep their settings.</summary>
    public static string LocalSupport => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Where the file picker should open: Bambu Studio's folder when there is one, else AppData.</summary>
    public static string SuggestedFolder
    {
        get
        {
            foreach (var (folder, _) in KnownApps)
            {
                var dir = Path.Combine(LocalSupport, folder);
                if (Directory.Exists(dir)) return dir;
            }
            return LocalSupport;
        }
    }

    /// <summary>Every printer with a stored access code in this PC's slicers.</summary>
    public static List<SlicerPrinter> Scan() => string.IsNullOrEmpty(LocalSupport) ? [] : ReadSupportFolder(LocalSupport);

    private static List<SlicerPrinter> ReadSupportFolder(string supportFolder)
    {
        var found = new List<SlicerPrinter>();
        foreach (var (folder, title) in KnownApps)
        {
            var path = Path.Combine(supportFolder, folder, folder + ".conf");
            foreach (var printer in Read(path, title))
            {
                if (!found.Any(p => p.Serial == printer.Serial)) found.Add(printer);
            }
        }
        return found;
    }

    /// <summary>Reads a settings file, or a folder holding one (Bambu Studio's own folder, AppData, a home folder or a
    /// whole disk, from Windows or a Mac), as picked or dropped by the person.</summary>
    public static List<SlicerPrinter> ReadPicked(string path)
    {
        if (File.Exists(path))
        {
            var source = Path.GetFileName(path).StartsWith("orca", StringComparison.OrdinalIgnoreCase) ? "OrcaSlicer" : "Bambu Studio";
            return Read(path, $"{source} settings file");
        }
        if (!Directory.Exists(path)) return [];
        var candidates = new List<string>
        {
            path,
            Path.Combine(path, "AppData", "Roaming"),
            Path.Combine(path, "Library", "Application Support"),
        };
        candidates.AddRange(KnownApps.Select(a => Path.Combine(path, a.Folder)));
        foreach (var candidate in candidates)
        {
            // Bambu Studio's own folder: the .conf sits right inside it.
            foreach (var (folder, title) in KnownApps)
            {
                var printers = Read(Path.Combine(candidate, folder + ".conf"), title);
                if (printers.Count > 0) return printers;
            }
            var inSupport = ReadSupportFolder(candidate);
            if (inSupport.Count > 0) return inSupport;
        }
        // A whole disk: C:\Users\<name>\AppData\Roaming, or a Mac's /Users/<name>/Library/Application Support.
        var users = Path.Combine(path, "Users");
        if (Directory.Exists(users))
        {
            string[] homes;
            try { homes = Directory.GetDirectories(users); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { homes = []; }
            foreach (var home in homes)
            {
                foreach (var support in new[] { Path.Combine(home, "AppData", "Roaming"), Path.Combine(home, "Library", "Application Support") })
                {
                    var printers = ReadSupportFolder(support);
                    if (printers.Count > 0) return printers;
                }
            }
        }
        return [];
    }

    /// <summary>Parses one config file.</summary>
    public static List<SlicerPrinter> Read(string path, string source)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return [];
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        // Drop anything after the JSON object (the "# MD5 checksum …" line some builds append).
        var end = text.LastIndexOf('}');
        if (end >= 0) text = text[..(end + 1)];
        JsonObject? json;
        try { json = JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return []; }
        if (json is null) return [];

        Dictionary<string, string> Section(string name)
        {
            var output = new Dictionary<string, string>();
            if (json[name] is not JsonObject dict) return output;
            foreach (var (key, value) in dict)
            {
                var s = (value is JsonValue v && v.TryGetValue<string>(out var str) ? str : "").Trim();
                if (s.Length > 0) output[key] = s;
            }
            return output;
        }

        var cloud = Section("access_code");
        var typed = Section("user_access_code");
        var uuid = (json["app"] as JsonObject)?["slicer_uuid"] is JsonValue u && u.TryGetValue<string>(out var id) ? id : "";
        var ips = Section("ip_address");
        var typedIps = Section("user_access_dev_ip")
            .Select(kv => (kv.Key, Ip: DecodeIp(kv.Value, uuid)))
            .Where(x => x.Ip is not null)
            .ToDictionary(x => x.Key, x => x.Ip!);
        var serials = cloud.Keys.Union(typed.Keys).Order(StringComparer.Ordinal);
        var result = new List<SlicerPrinter>();
        foreach (var serial in serials)
        {
            var codes = new List<string>();
            foreach (var code in new[] { cloud.GetValueOrDefault(serial), typed.GetValueOrDefault(serial) })
            {
                if (code is not null && !codes.Contains(code)) codes.Add(code);
            }
            if (codes.Count == 0) continue;
            result.Add(new SlicerPrinter
            {
                Serial = serial, AccessCodes = codes, Host = typedIps.GetValueOrDefault(serial) ?? ips.GetValueOrDefault(serial),
                Name = null, Source = source,
            });
        }
        return result;
    }

    /// <summary>Undoes <c>BBLCrossTalk::Encode_DevIp</c> (BambuStudio <c>BBLUtil.cpp</c>): hex of the IP XORed with the low byte of
    /// successive <c>std::mt19937</c> outputs, seeded with the 32-bit FNV-1a hash of the slicer's UUID. Plain IPs
    /// (older versions, OrcaSlicer) come back as they are.</summary>
    public static string? DecodeIp(string value, string key)
    {
        static bool IsAddress(string text) =>
            text.Length > 0 && text.Length <= 64 && text.All(c => c < 128 && (char.IsAsciiLetterOrDigit(c) || ".:-".Contains(c)))
            && text.Any(c => c is '.' or ':');

        if (IsAddress(value) && value.Contains('.')) return value;
        if (key.Length == 0 || value.Length % 2 != 0 || !value.All(char.IsAsciiHexDigit)) return IsAddress(value) ? value : null;
        uint seed = 2_166_136_261;
        foreach (var b in Encoding.UTF8.GetBytes(key)) seed = unchecked((seed ^ b) * 16_777_619);
        var rng = new MersenneTwister(seed);
        var bytes = new byte[value.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(Convert.ToByte(value.Substring(i * 2, 2), 16) ^ (byte)rng.Next());
        var plain = Encoding.UTF8.GetString(bytes);
        return IsAddress(plain) ? plain : null;
    }
}

/// <summary>The 32-bit Mersenne Twister, matching C++'s <c>std::mt19937</c>.</summary>
public sealed class MersenneTwister
{
    private readonly uint[] _state = new uint[624];
    private int _index = 624;

    public MersenneTwister(uint seed)
    {
        _state[0] = seed;
        for (var i = 1; i < 624; i++) _state[i] = unchecked(1_812_433_253u * (_state[i - 1] ^ (_state[i - 1] >> 30)) + (uint)i);
    }

    public uint Next()
    {
        if (_index >= 624)
        {
            for (var i = 0; i < 624; i++)
            {
                var y = (_state[i] & 0x8000_0000u) | (_state[(i + 1) % 624] & 0x7FFF_FFFFu);
                _state[i] = _state[(i + 397) % 624] ^ (y >> 1) ^ ((y & 1) == 0 ? 0u : 0x9908_B0DFu);
            }
            _index = 0;
        }
        var x = _state[_index++];
        x ^= x >> 11;
        x ^= (x << 7) & 0x9D2C_5680u;
        x ^= (x << 15) & 0xEFC6_0000u;
        x ^= x >> 18;
        return x;
    }
}
