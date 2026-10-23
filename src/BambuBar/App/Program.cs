using System.IO;
using System.Windows;
using System.Windows.Threading;
using BambuBar.Platform;
using BambuBar.Services;

namespace BambuBar.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppArguments.Current = new AppArguments(args);

        if (Probe.Parse(AppArguments.Current, serial => new CredentialStore().AccessCode(serial)) is { } probe)
            return RunProbe(probe);

        // One copy per Windows account. Developer runs (demo, snapshots, the emulator) get their own, so they can run
        // beside the real app.
        var name = AppArguments.Current.IsDeveloperRun ? $"BambuBar-dev-{Environment.ProcessId}" : "BambuBar";
        using var instance = new SingleInstance(name);
        if (!instance.IsFirst)
        {
            instance.Forward(args);
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var context = new DispatcherSynchronizationContext(app.Dispatcher);
        SynchronizationContext.SetSynchronizationContext(context);
        MainThread.Initialize(context);
        var host = new AppHost(app, instance);
        app.Startup += (_, _) => host.Start();
        app.DispatcherUnhandledException += (_, e) =>
        {
            // Keep running in the tray; one bad report shouldn't take the app down. The crash log helps bug reports.
            Log(e.Exception);
            e.Handled = true;
        };
        return app.Run();
    }

    /// <summary>The console probe. A Windows app has no console of its own, so it writes to the one it was started
    /// from (or to a pipe or file it was redirected to).</summary>
    private static int RunProbe(Probe.Arguments probe)
    {
        if (!Console.IsOutputRedirected) Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
        var output = Console.IsOutputRedirected ? Console.Out : new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var dispatcher = Dispatcher.CurrentDispatcher;
        var context = new DispatcherSynchronizationContext(dispatcher);
        SynchronizationContext.SetSynchronizationContext(context);
        MainThread.Initialize(context);
        var code = 1;
        dispatcher.BeginInvoke(() => Probe.Start(probe, output, exit =>
        {
            code = exit;
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }));
        Dispatcher.Run();
        output.Flush();
        Native.FreeConsole();
        return code;
    }

    private static void Log(Exception e)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDirectory, "errors.log");
            File.AppendAllText(path, $"{DateTime.Now:O} {e}\n\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
