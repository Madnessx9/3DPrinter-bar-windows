namespace BambuBar.Services;

/// <summary>The printers' LAN access codes, per serial number. Windows Credential Manager in the app.</summary>
public interface ICredentialStore
{
    string? AccessCode(string serial);
    void SetAccessCode(string code, string serial);
    void RemoveAccessCode(string serial);
}

/// <summary>Keeps codes in memory only: demo, emulator and test runs, which must never touch the real store.</summary>
public sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _codes = [];
    public string? AccessCode(string serial) => _codes.GetValueOrDefault(serial);
    public void SetAccessCode(string code, string serial) => _codes[serial] = code;
    public void RemoveAccessCode(string serial) => _codes.Remove(serial);
}

/// <summary>Posts print notifications (finished, failed, paused, errors).</summary>
public interface INotifier
{
    void RequestAuthorization();
    /// <summary>The same <paramref name="id"/> replaces the previous notification of its kind instead of piling up.</summary>
    void Post(string title, string body, string id);
}

public sealed class NullNotifier : INotifier
{
    public void RequestAuthorization() { }
    public void Post(string title, string body, string id) { }
}

/// <summary>Where Bambu Bar keeps its files: <c>%APPDATA%\Bambu Bar</c>, or <c>BAMBUBAR_DATA_DIR</c> when set (tests).</summary>
public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("BAMBUBAR_DATA_DIR");
            var dir = string.IsNullOrEmpty(overridden)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bambu Bar")
                : overridden;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}

/// <summary>The command line, parsed once at startup.</summary>
public sealed class AppArguments
{
    public static AppArguments Current { get; set; } = new([]);

    private readonly string[] _args;
    public AppArguments(string[] args) => _args = args;

    public bool Has(string flag) => _args.Contains(flag, StringComparer.OrdinalIgnoreCase);

    /// <summary>The <paramref name="count"/> values after <paramref name="flag"/>, or null when there aren't that many.</summary>
    public string[]? Values(string flag, int count)
    {
        var i = Array.FindIndex(_args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + count >= _args.Length) return null;
        return _args[(i + 1)..(i + 1 + count)];
    }

    /// <summary>The value after <paramref name="flag"/> if it isn't another flag.</summary>
    public string? Value(string flag)
    {
        var i = Array.FindIndex(_args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= _args.Length || _args[i + 1].StartsWith("--", StringComparison.Ordinal)) return null;
        return _args[i + 1];
    }

    public IReadOnlyList<string> All => _args;

    /// <summary><c>--background</c> starts the app the way a login launch does: tray only, no window.</summary>
    public bool Background => Has("--background");

    /// <summary><c>--insecure-tls</c>: accept any certificate (the local printer emulator only).</summary>
    public bool InsecureTls => Has("--insecure-tls");

    /// <summary>Demo, showcase, snapshot, probe and emulator runs: no update checks, nothing saved.</summary>
    public bool IsDeveloperRun => new[] { "--demo", "--snapshot", "--probe", "--connect", "--emulator" }.Any(Has);

    /// <summary>Demo and developer sessions never touch the real history, printers or codes.</summary>
    public bool IsEphemeral => Has("--demo") || Has("--connect") || Has("--emulator") || Has("--snapshot");
}
