using BambuBar.Models;
using BambuBar.Services;

namespace BambuBar.Tests;

public class StoreTests
{
    private sealed class RecordingNotifier : INotifier
    {
        public List<(string Title, string Body, string Id)> Posted { get; } = [];
        public void RequestAuthorization() { }
        public void Post(string title, string body, string id) => Posted.Add((title, body, id));
    }

    private static readonly TestMainThread Main = TestMainThread.Shared;

    private static (PrinterStore, PrinterSession, RecordingNotifier) MakeStore()
    {
        var notifier = new RecordingNotifier();
        var store = new PrinterStore(new SettingsStore(null), new MemoryCredentialStore(), notifier, new HistoryStore(null));
        var session = store.Add(new PrinterConfig { Host = "192.0.2.1", Serial = "01P00A000000001", Name = "Workshop", ModelCode = "C11" });
        return (store, session, notifier);
    }

    private static PrinterStatus Status(JobState state, int progress, string job = "Benchy")
    {
        var s = SamplePrinter.Status();
        s.State = state;
        s.Progress = progress;
        s.JobName = job;
        s.TaskId = "42";
        return s;
    }

    [Fact]
    public void AJobInProgressAtLaunchIsRecordedButNotAnnounced()
    {
        Main.Run(() =>
        {
            var (store, session, notifier) = MakeStore();
            session.ShowDemoStatus(Status(JobState.Printing, 50));
            Assert.Empty(notifier.Posted);
            var job = Assert.Single(store.History);
            Assert.Equal(JobOutcome.Running, job.Outcome);
            Assert.Equal("Benchy", job.Name);
            // Half done with time left: it started a while ago.
            Assert.True(job.StartedAt < DateTime.UtcNow.AddMinutes(-1));
        });
    }

    [Fact]
    public void FinishingNotifiesOnce()
    {
        Main.Run(() =>
        {
            var (store, session, notifier) = MakeStore();
            session.ShowDemoStatus(Status(JobState.Printing, 98));
            session.ShowDemoStatus(Status(JobState.Printing, 99));
            session.ShowDemoStatus(Status(JobState.Finished, 100));
            session.ShowDemoStatus(Status(JobState.Finished, 100));
            var (title, body, _) = Assert.Single(notifier.Posted);
            Assert.Equal("Print finished", title);
            Assert.StartsWith("Benchy finished", body);
            Assert.Equal(JobOutcome.Finished, Assert.Single(store.History).Outcome);
        });
    }

    [Fact]
    public void PausesAndNewJobsAreTracked()
    {
        Main.Run(() =>
        {
            var (store, session, notifier) = MakeStore();
            store.NotifyStarted = true;
            session.ShowDemoStatus(Status(JobState.Idle, 0, ""));
            session.ShowDemoStatus(Status(JobState.Printing, 1));
            session.ShowDemoStatus(Status(JobState.Paused, 5));
            var cancel = Status(JobState.Failed, 5);
            cancel.Canceled = true;
            session.ShowDemoStatus(cancel);
            Assert.Equal(["Print started", "Print paused", "Print canceled"], notifier.Posted.Select(p => p.Title));
            Assert.Equal(JobOutcome.Canceled, Assert.Single(store.History).Outcome);
        });
    }

    [Fact]
    public void TitlesNameThePrinterOnceThereAreSeveral()
    {
        Main.Run(() =>
        {
            var (store, session, notifier) = MakeStore();
            store.Add(new PrinterConfig { Host = "192.0.2.2", Serial = "01S00C000000002", Name = "Garage", ModelCode = "C12" });
            session.ShowDemoStatus(Status(JobState.Printing, 99));
            session.ShowDemoStatus(Status(JobState.Failed, 99));
            Assert.Equal("Workshop: Print failed", Assert.Single(notifier.Posted).Title);
        });
    }

    [Fact]
    public void MoonrakerStatusParsingAndSnapmakerU1Model()
    {
        var config = new PrinterConfig
        {
            Host = "192.168.1.60",
            Serial = "SMU-192-168-1-60",
            Name = "Snapmaker U1",
            ModelCode = "SM-U1",
            Protocol = PrinterProtocol.Moonraker,
        };
        Assert.Equal("Snapmaker U1", config.FullModelName);
        Assert.Equal(80, config.Endpoint.Port);

        var objects = System.Text.Json.Nodes.JsonNode.Parse("""
        {
            "print_stats": {
                "state": "printing",
                "filename": "Voron_Cube_v4.gcode",
                "print_duration": 1800,
                "info": { "current_layer": 45, "total_layer": 90 }
            },
            "virtual_sdcard": { "progress": 0.50 },
            "extruder": { "temperature": 215.4, "target": 215.0, "state": "PARKED" },
            "extruder1": { "temperature": 210.1, "target": 210.0, "state": "ACTIVATE" },
            "extruder2": { "temperature": 25.0, "target": 0.0, "state": "PARKED" },
            "extruder3": { "temperature": 25.0, "target": 0.0, "state": "PARKED" },
            "print_task_config": {
                "filament_vendor": ["Generic", "Generic", "Generic", "SUNLU"],
                "filament_type": ["PLA", "PLA", "PLA", "PLA"],
                "filament_sub_type": ["", "", "", "NONE"],
                "filament_color_rgba": ["000000FF", "E72F1DFF", "FFFFFFFF", "8B4513FF"],
                "filament_exist": [true, true, true, true],
                "filament_official": [false, false, false, true]
            },
            "heater_bed": { "temperature": 60.0, "target": 60.0 },
            "temperature_sensor cavity": { "temperature": 37.2 },
            "fan": { "speed": 0.8 },
            "gcode_move": { "speed_factor": 1.0 }
        }
        """)!.AsObject();

        var meta = System.Text.Json.Nodes.JsonNode.Parse("""
        {
            "estimated_time": 3600,
            "layer_count": 90
        }
        """)!.AsObject();

        var parsed = BambuBar.Protocol.MoonrakerConnection.ParseMoonrakerStatus(objects, meta, "v0.12.0-paxx12", null, config);
        Assert.Equal(JobState.Printing, parsed.State);
        Assert.Equal("Voron_Cube_v4", parsed.JobName);
        Assert.Equal(50, parsed.Progress);
        Assert.Equal(30, parsed.RemainingMinutes);
        Assert.Equal(45, parsed.Layer);
        Assert.Equal(90, parsed.TotalLayers);
        Assert.Equal(210.1, parsed.Nozzle.Current);
        Assert.NotNull(parsed.SecondNozzle);
        Assert.Equal(215.4, parsed.SecondNozzle!.Current);
        Assert.Equal(60.0, parsed.Bed.Current);
        Assert.Equal(37.2, parsed.Chamber);
        Assert.Equal(80, parsed.PartFan);
        Assert.Equal("Toolheads", parsed.Ams[0].Title);
        Assert.Equal(4, parsed.Ams[0].Spools.Count);
        Assert.Equal(1, parsed.ActiveSpoolId);
        Assert.Equal(["T1", "T2", "T3", "T4"], parsed.Ams[0].Spools.Select(s => s.Label));
        Assert.Equal(["000000FF", "E72F1DFF", "FFFFFFFF", "8B4513FF"], parsed.Ams[0].Spools.Select(s => s.Color));
        Assert.Equal(["Black", "Red", "White", "Brown"], parsed.Ams[0].Spools.Select(s => s.ColorName));
        Assert.Equal("SUNLU", parsed.Ams[0].Spools[3].Brand);
    }

    [Fact]
    public void ActiveSessionsFilterOnlyReturnsActiveOrAttentionPrinters()
    {
        Main.Run(() =>
        {
            var (store, session1, _) = MakeStore();
            var session2 = store.Add(new PrinterConfig { Host = "192.168.1.60", Serial = "SMU-1", Name = "Snapmaker U1", ModelCode = "SM-U1", Protocol = PrinterProtocol.Moonraker });
            session1.ShowDemoStatus(Status(JobState.Idle, 0, ""));
            session2.ShowDemoStatus(Status(JobState.Printing, 42, "MultiColorDragon"));

            Assert.Equal(2, store.FleetSessions.Count);
            Assert.Single(store.ActiveSessions);
            Assert.Equal("Snapmaker U1", store.ActiveSessions[0].Config.Name);

            store.FleetActiveOnly = true;
            Assert.Single(store.DisplayedFleetSessions);
            Assert.Equal("Snapmaker U1", store.DisplayedFleetSessions[0].Config.Name);
        });
    }
}
