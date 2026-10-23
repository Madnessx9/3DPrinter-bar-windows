using System.Collections.Concurrent;
using BambuBar.Services;

namespace BambuBar.Tests;

/// <summary>A single thread with a message queue, standing in for the app's UI thread.</summary>
public sealed class TestMainThread : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = new();
    private readonly Thread _thread;

    public static TestMainThread Shared { get; } = new();

    private TestMainThread()
    {
        _thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        }) { IsBackground = true, Name = "Test main thread" };
        _thread.Start();
        MainThread.Initialize(this);
    }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    /// <summary>Runs <paramref name="action"/> on the main thread and waits for it.</summary>
    public T Run<T>(Func<T> action)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            try { done.SetResult(action()); }
            catch (Exception e) { done.SetException(e); }
        }, null);
        return done.Task.GetAwaiter().GetResult();
    }

    public void Run(Action action) => Run(() => { action(); return 0; });

    /// <summary>Polls <paramref name="condition"/> on the main thread until it holds.</summary>
    public async Task<bool> WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Run(condition)) return true;
            await Task.Delay(50);
        }
        return Run(condition);
    }

    public void Dispose() => _queue.CompleteAdding();
}
