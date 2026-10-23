using System.IO;
using System.IO.Pipes;
using System.Text;

namespace BambuBar.Platform;

/// <summary>Keeps one copy of the app per Windows account. Starting it again hands the new command line to the
/// running copy (which shows its window) and exits.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    public bool IsFirst { get; }

    /// <summary>Raised on a thread-pool thread with another launch's arguments.</summary>
    public event Action<string[]>? Received;

    public SingleInstance(string name)
    {
        var scoped = $"{name}-{Environment.UserName}";
        _pipeName = scoped;
        _mutex = new Mutex(true, $"Local\\{scoped}", out var created);
        IsFirst = created;
    }

    public void Listen() => _ = Task.Run(ListenLoop);

    private async Task ListenLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(_stop.Token);
                Received?.Invoke(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { await Task.Delay(200); }
        }
    }

    /// <summary>Sends this launch's arguments to the running copy.</summary>
    public bool Forward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            var bytes = Encoding.UTF8.GetBytes(string.Join('\n', args.Length == 0 ? ["--show"] : args));
            client.Write(bytes);
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (IsFirst)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
