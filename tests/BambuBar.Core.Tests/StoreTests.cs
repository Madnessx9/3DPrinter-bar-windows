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
}
