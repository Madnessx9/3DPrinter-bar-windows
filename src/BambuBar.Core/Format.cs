using System.Globalization;

namespace BambuBar;

public static class Format
{
    /// <summary>"2h 05m", "45m", "&lt;1m".</summary>
    public static string Duration(int minutes)
    {
        if (minutes <= 0) return "<1m";
        int h = minutes / 60, m = minutes % 60;
        if (h == 0) return $"{m}m";
        if (h >= 24) return $"{h / 24}d {h % 24}h";
        return $"{h}h {m:00}m";
    }

    public static string Duration(TimeSpan span) => Duration((int)Math.Round(span.TotalMinutes, MidpointRounding.AwayFromZero));

    /// <summary>The time a job finishes: "14:32", "tomorrow 08:10", "Thu 08:10". Takes and compares local times.</summary>
    public static string Finish(DateTime utc, DateTime? nowUtc = null)
    {
        var date = utc.ToLocalTime();
        var now = (nowUtc ?? DateTime.UtcNow).ToLocalTime();
        var time = date.ToString("t", CultureInfo.CurrentCulture);
        if (date.Date == now.Date) return time;
        if (date.Date == now.Date.AddDays(1)) return $"tomorrow {time}";
        return date.ToString("ddd", CultureInfo.CurrentCulture) + " " + time;
    }

    public static string Temperature(double value) => $"{(int)Math.Round(value, MidpointRounding.AwayFromZero)}°";

    /// <summary>"≈ 810 g", "1 kg"; <paramref name="approximate"/> for measured amounts (the AMS estimates what's left).</summary>
    public static string Grams(int value, bool approximate = true)
    {
        var text = value >= 1000 && value % 1000 == 0 ? $"{value / 1000} kg"
            : value >= 1000 ? (value / 1000.0).ToString("0.00", CultureInfo.CurrentCulture) + " kg"
            : $"{value} g";
        return approximate ? "≈ " + text : text;
    }

    /// <summary>"3 min. ago", "2 hr. ago", "yesterday": a short relative time like the macOS app's.</summary>
    public static string Relative(DateTime utc, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var delta = now - utc;
        var future = delta < TimeSpan.Zero;
        var span = future ? -delta : delta;
        string amount;
        if (span.TotalSeconds < 60) amount = $"{Math.Max(0, (int)span.TotalSeconds)} sec.";
        else if (span.TotalMinutes < 60) amount = $"{(int)span.TotalMinutes} min.";
        else if (span.TotalHours < 24) amount = $"{(int)span.TotalHours} hr.";
        else if (span.TotalDays < 2 && !future) return "yesterday";
        else if (span.TotalDays < 7) amount = $"{(int)span.TotalDays} days";
        else if (span.TotalDays < 30) amount = $"{(int)(span.TotalDays / 7)} wk.";
        else if (span.TotalDays < 365) amount = $"{(int)(span.TotalDays / 30)} mo.";
        else amount = $"{(int)(span.TotalDays / 365)} yr.";
        return future ? $"in {amount}" : $"{amount} ago";
    }
}

/// <summary>Parses Bambu colours: <c>RRGGBB</c> or <c>RRGGBBAA</c>.</summary>
public static class BambuColor
{
    /// <summary>The colour as (r, g, b, a) bytes, alpha raised to at least 35 %; null for fully transparent (unset slots).</summary>
    public static (byte R, byte G, byte B, byte A)? Parse(string? hex)
    {
        if (hex is null) return null;
        var clean = new string(hex.Where(char.IsAsciiLetterOrDigit).ToArray());
        if ((clean.Length != 6 && clean.Length != 8) || !ulong.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return null;
        byte r, g, b, a;
        if (clean.Length == 8)
        {
            r = (byte)(v >> 24); g = (byte)(v >> 16); b = (byte)(v >> 8); a = (byte)v;
        }
        else
        {
            r = (byte)(v >> 16); g = (byte)(v >> 8); b = (byte)v; a = 255;
        }
        if (a == 0) return null;
        return (r, g, b, (byte)Math.Max(a, (byte)Math.Round(0.35 * 255)));
    }

    /// <summary>Perceived brightness of a <c>RRGGBBAA</c> colour, 0-1, for picking legible text on a swatch.</summary>
    public static double Luminance(string? hex)
    {
        var clean = new string((hex ?? "").Where(char.IsAsciiLetterOrDigit).ToArray());
        if (clean.Length < 6 || !ulong.TryParse(clean[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return 0.5;
        double r = ((v >> 16) & 0xFF) / 255.0, g = ((v >> 8) & 0xFF) / 255.0, b = (v & 0xFF) / 255.0;
        return 0.299 * r + 0.587 * g + 0.114 * b;
    }
}

public static class WiFi
{
    public static string Describe(int dbm) => dbm switch
    {
        >= -55 and <= 0 => "Wi-Fi excellent",
        >= -67 and < -55 => "Wi-Fi good",
        >= -75 and < -67 => "Wi-Fi fair",
        >= -89 and < -75 => "Wi-Fi weak",
        -90 => "Ethernet",
        _ => "Wi-Fi poor",
    };
}

public static class NozzleType
{
    /// <summary>"HS01" → "Hardened steel", "HH01" → "Hardened steel, high flow".</summary>
    public static string Describe(string code)
    {
        var upper = code.ToUpperInvariant();
        if (upper.Length < 4)
            return upper.Contains("HARDENED") ? "Hardened steel" : upper.Contains("STAINLESS") ? "Stainless steel" : code;
        var material = upper.Substring(2, 2) switch
        {
            "00" => "Stainless steel",
            "01" => "Hardened steel",
            "05" => "Tungsten carbide",
            _ => code,
        };
        return upper[1] switch
        {
            'U' => material + ", TPU high flow",
            'H' or 'E' => material + ", high flow",
            _ => material,
        };
    }
}
