using System.Diagnostics;
using System.Net.Sockets;

namespace BambuBar.Tests;

/// <summary>
/// Runs scripts/emulator/printer.py (a TLS mosquitto broker posing as a P2S) for the duration of the tests.
/// Skipped where mosquitto or openssl isn't installed.
/// </summary>
public sealed class EmulatorFixture : IDisposable
{
    public const string Serial = "22E00EMULATOR0", Code = "12345678";
    public const int Port = 18883;
    public string? Unavailable { get; }
    public string CertificateFolder { get; }
    private readonly Process? _process;
    private readonly string _folder;

    public EmulatorFixture()
    {
        // A copy, so the generated certificates stay out of the source tree.
        _folder = Directory.CreateTempSubdirectory("bambubar-emulator").FullName;
        CertificateFolder = Path.Combine(_folder, "certs");
        var source = Path.Combine(RepositoryRoot(), "scripts", "emulator");
        foreach (var file in new[] { "printer.py", "p2s-sample.json" }) File.Copy(Path.Combine(source, file), Path.Combine(_folder, file));
        if (!OnPath("mosquitto") && !File.Exists("/usr/sbin/mosquitto")) { Unavailable = "mosquitto isn't installed"; return; }
        if (!OnPath("openssl")) { Unavailable = "openssl isn't installed"; return; }
        if (!OnPath("mosquitto_pub")) { Unavailable = "mosquitto_pub isn't installed (mosquitto-clients)"; return; }
        var start = new ProcessStartInfo("python3", ["-u", Path.Combine(_folder, "printer.py")])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        start.Environment["PORT"] = Port.ToString();
        start.Environment["SPEED"] = "20";
        _process = Process.Start(start);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !_process!.HasExited)
        {
            try
            {
                using var probe = new TcpClient();
                probe.Connect("127.0.0.1", Port);
                Thread.Sleep(500);
                return;
            }
            catch (SocketException) { Thread.Sleep(200); }
        }
        Unavailable = "the emulator didn't start: " + (_process!.HasExited ? _process.StandardError.ReadToEnd() : "timed out");
    }

    private static bool OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(dir => File.Exists(Path.Combine(dir, tool)));

    public static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "scripts", "emulator"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("scripts/emulator not found");
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException) { }
        // The broker is a child of python and keeps running if only python dies.
        foreach (var broker in Process.GetProcessesByName("mosquitto"))
        {
            try { if (broker.StartInfo is not null && CommandLine(broker).Contains(_folder)) broker.Kill(); } catch { }
        }
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private static string CommandLine(Process p)
    {
        try { return File.ReadAllText($"/proc/{p.Id}/cmdline"); } catch { return ""; }
    }
}

[CollectionDefinition("Emulator")]
public sealed class EmulatorCollection : ICollectionFixture<EmulatorFixture>;
