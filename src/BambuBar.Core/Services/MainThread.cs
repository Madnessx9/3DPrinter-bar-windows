namespace BambuBar.Services;

/// <summary>
/// The app's main (UI) thread, where printer state lives and changes, like <c>@MainActor</c> in the macOS app.
/// Network code runs on the thread pool and hands its events here.
/// </summary>
public static class MainThread
{
    private static SynchronizationContext? _context;

    /// <summary>Captures the calling thread's context. The app calls this on its UI thread at startup.</summary>
    public static void Initialize(SynchronizationContext context) => _context = context;

    public static bool IsInitialized => _context is not null;

    public static void Post(Action action)
    {
        var context = _context ?? throw new InvalidOperationException("MainThread.Initialize hasn't been called.");
        context.Post(static state => ((Action)state!)(), action);
    }
}

/// <summary>A one-shot or repeating timer whose callback runs on the main thread.</summary>
public sealed class MainTimer : IDisposable
{
    private readonly Timer _timer;
    private readonly Action _action;
    private volatile bool _disposed;

    private MainTimer(TimeSpan due, TimeSpan period, Action action)
    {
        _action = action;
        _timer = new Timer(_ => MainThread.Post(Fire), null, due, period);
    }

    private void Fire()
    {
        if (!_disposed) _action();
    }

    public static MainTimer After(TimeSpan delay, Action action) =>
        new(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan, action);

    public static MainTimer Every(TimeSpan interval, Action action) => new(interval, interval, action);

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}

/// <summary>Sleep and wake, raised by the app from the operating system's power notifications.</summary>
public static class PowerEvents
{
    public static event Action? WillSleep;
    public static event Action? DidWake;

    public static void RaiseWillSleep() => WillSleep?.Invoke();
    public static void RaiseDidWake() => DidWake?.Invoke();
}
