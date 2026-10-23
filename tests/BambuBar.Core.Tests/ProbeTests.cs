using BambuBar.Services;

namespace BambuBar.Tests;

[Collection("Emulator")]
public class ProbeTests(EmulatorFixture emulator)
{
    private static readonly TestMainThread Main = TestMainThread.Shared;

    [Fact]
    public void ParsesTheCommandLine()
    {
        var args = new AppArguments(["--probe", "192.168.1.50", "01P00A000000000", "saved", "5", "--insecure-tls", "--raw-filament"]);
        var probe = Probe.Parse(args, serial => serial == "01P00A000000000" ? "12345678" : null);
        Assert.Equal(new Probe.Arguments("192.168.1.50", "01P00A000000000", "12345678", 5, true, true), probe);
        Assert.Null(Probe.Parse(new AppArguments(["--probe", "host"]), _ => null));
        Assert.Equal(20, Probe.Parse(new AppArguments(["--probe", "h", "s", "c"]), _ => null)!.Seconds);
    }

    [Fact]
    public async Task ReportsTheEmulatorsStatus()
    {
        Assert.SkipWhen(emulator.Unavailable is not null, emulator.Unavailable ?? "");
        var output = new StringWriter();
        var exit = new TaskCompletionSource<int>();
        var probe = new Probe.Arguments($"127.0.0.1:{EmulatorFixture.Port}", EmulatorFixture.Serial, EmulatorFixture.Code, 3, Insecure: true, RawFilament: true);
        Main.Run(() => Probe.Start(probe, output, exit.SetResult));
        Assert.Equal(0, await exit.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        var log = output.ToString();
        Assert.Contains("phase: Online", log);
        Assert.Contains("status #1:", log);
        Assert.Contains("Planetary Gearbox", log);
        Assert.Contains("raw filament:", log);
        Assert.Contains("\"ams\"", log);
    }
}
