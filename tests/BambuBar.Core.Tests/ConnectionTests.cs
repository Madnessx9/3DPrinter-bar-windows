using System.Security.Cryptography.X509Certificates;
using BambuBar.Models;
using BambuBar.Protocol;
using BambuBar.Services;

namespace BambuBar.Tests;

[Collection("Emulator")]
public class ConnectionTests(EmulatorFixture emulator)
{
    private static readonly TestMainThread Main = TestMainThread.Shared;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private PrinterConfig Config => new() { Host = $"127.0.0.1:{EmulatorFixture.Port}", Serial = EmulatorFixture.Serial, ModelCode = "N7", Name = "Emulator" };

    [Fact]
    public async Task ConnectsAndFollowsTheJob()
    {
        Assert.SkipWhen(emulator.Unavailable is not null, emulator.Unavailable ?? "");
        var connection = Main.Run(() => new PrinterConnection());
        PrinterStatus? status = null;
        var acks = new List<(string, bool)>();
        Main.Run(() =>
        {
            connection.StatusChanged += (s, _) => status = s;
            connection.Acknowledged += (command, ok, _) => acks.Add((command, ok));
            connection.Start(Config, EmulatorFixture.Code, insecure: true);
        });
        try
        {
            Assert.True(await Main.WaitFor(() => connection.Phase is ConnectionPhase.Online, Timeout), $"phase {Main.Run(() => connection.Phase)}");
            Assert.True(await Main.WaitFor(() => status is not null, Timeout));
            Assert.Equal("Planetary Gearbox", Main.Run(() => status!.JobName));
            Assert.True(Main.Run(() => status!.Ams.Count) > 0);

            // The emulator heats up, then prints.
            Assert.True(await Main.WaitFor(() => status!.State == JobState.Printing && status.Progress > 1, Timeout));

            Main.Run(connection.Pause);
            Assert.True(await Main.WaitFor(() => status!.State == JobState.Paused, Timeout));
            Main.Run(connection.Resume);
            Assert.True(await Main.WaitFor(() => status!.State == JobState.Printing, Timeout));

            Main.Run(() => connection.SetSpeed(SpeedLevel.Sport));
            Assert.True(await Main.WaitFor(() => status!.Speed == SpeedLevel.Sport, Timeout));

            var light = Main.Run(() => status!.ChamberLightOn ?? false);
            Main.Run(() => connection.SetChamberLight(!light));
            Assert.True(await Main.WaitFor(() => status!.ChamberLightOn == !light, Timeout));
            Assert.True(await Main.WaitFor(() => acks.Any(a => a.Item1 == "pause" && a.Item2), Timeout));
        }
        finally
        {
            Main.Run(connection.Dispose);
        }
    }

    [Fact]
    public async Task AWrongAccessCodeIsReported()
    {
        Assert.SkipWhen(emulator.Unavailable is not null, emulator.Unavailable ?? "");
        var connection = Main.Run(() => new PrinterConnection());
        Main.Run(() => connection.Start(Config, "00000000", insecure: true));
        try
        {
            Assert.True(await Main.WaitFor(() => connection.Phase is ConnectionPhase.Unauthorized, Timeout), $"phase {Main.Run(() => connection.Phase)}");
        }
        finally { Main.Run(connection.Dispose); }
    }

    [Fact]
    public async Task AnUntrustedCertificateIsRefused()
    {
        Assert.SkipWhen(emulator.Unavailable is not null, emulator.Unavailable ?? "");
        // The emulator's own CA isn't one of Bambu's, so a normal (verifying) connection must refuse it.
        var connection = Main.Run(() => new PrinterConnection());
        Main.Run(() => connection.Start(Config, EmulatorFixture.Code));
        try
        {
            Assert.True(await Main.WaitFor(() => connection.Phase is ConnectionPhase.CertificateRejected, Timeout), $"phase {Main.Run(() => connection.Phase)}");
        }
        finally { Main.Run(connection.Dispose); }
    }

    [Fact]
    public async Task ASessionNotifiesAboutTheJob()
    {
        Assert.SkipWhen(emulator.Unavailable is not null, emulator.Unavailable ?? "");
        var credentials = new MemoryCredentialStore();
        credentials.SetAccessCode("00000000", EmulatorFixture.Serial);
        var session = Main.Run(() => new PrinterSession(Config, credentials));
        // The first code is stale; the session falls back to the second, like a slicer holding two.
        Main.Run(() => session.Start("00000000", ["00000000", EmulatorFixture.Code], insecure: true));
        try
        {
            Assert.True(await Main.WaitFor(() => session.Phase is ConnectionPhase.Online && session.Status is not null, Timeout),
                $"phase {Main.Run(() => session.Phase)}");
            Assert.Equal(EmulatorFixture.Code, credentials.AccessCode(EmulatorFixture.Serial));
            Main.Run(session.ToggleLight);
            var expected = Main.Run(() => session.PendingLight);
            Assert.NotNull(expected);
            Assert.True(await Main.WaitFor(() => session.PendingLight is null && session.ChamberLightOn == expected, Timeout));
        }
        finally { Main.Run(session.Dispose); }
    }

    [Fact]
    public void TheEmulatorCertificateChecksAgainstItsOwnCa()
    {
        Assert.SkipWhen(emulator.Unavailable is not null, emulator.Unavailable ?? "");
        var leaf = BambuTrust.CertificatesFromPem(File.ReadAllText(Path.Combine(emulator.CertificateFolder, "server.pem"))).First();
        var ca = new X509Certificate2Collection(BambuTrust.CertificatesFromPem(File.ReadAllText(Path.Combine(emulator.CertificateFolder, "ca.pem"))).ToArray());
        Assert.IsType<BambuTrust.Verdict.Trusted>(BambuTrust.Check(leaf, null, EmulatorFixture.Serial, ca));
        Assert.IsType<BambuTrust.Verdict.Rejected>(BambuTrust.Check(leaf, null, "01P00A000000001", ca));
        Assert.IsType<BambuTrust.Verdict.Rejected>(BambuTrust.Evaluate(leaf, null, EmulatorFixture.Serial));
    }
}
