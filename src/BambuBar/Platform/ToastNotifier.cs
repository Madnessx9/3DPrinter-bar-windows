using BambuBar.Services;
using Microsoft.Toolkit.Uwp.Notifications;

namespace BambuBar.Platform;

/// <summary>Print notifications as Windows toasts (finished, failed, paused, errors). Clicking one opens the window
/// on the printer it's about.</summary>
public sealed class ToastNotifier : INotifier
{
    private bool _listening;

    /// <summary>Raised on the main thread with the printer serial a clicked toast was about (null if unknown).</summary>
    public event Action<string?>? Activated;

    public void RequestAuthorization()
    {
        // Windows needs no permission prompt; the person can turn Bambu Bar's notifications off in Settings → System →
        // Notifications. Listening for clicks is set up on first use.
        Listen();
    }

    public void Post(string title, string body, string id)
    {
        Listen();
        var serial = id.Contains('.') ? id[(id.LastIndexOf('.') + 1)..] : null;
        try
        {
            new ToastContentBuilder()
                .AddArgument("serial", serial ?? "")
                .AddText(title)
                .AddText(body)
                .Show(toast =>
                {
                    // Same tag replaces the previous notification of its kind instead of piling up.
                    toast.Tag = Tag(id);
                    toast.Group = "printer";
                });
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Notifications switched off by policy, or no notification platform (Windows Server Core): nothing to show.
        }
    }

    /// <summary>Toast tags are limited to 64 characters.</summary>
    private static string Tag(string id) => id.Length <= 64 ? id : id[^64..];

    private void Listen()
    {
        if (_listening) return;
        _listening = true;
        ToastNotificationManagerCompat.OnActivated += args =>
        {
            var parsed = ToastArguments.Parse(args.Argument);
            var serial = parsed.TryGetValue("serial", out var value) && value.Length > 0 ? value : null;
            MainThread.Post(() => Activated?.Invoke(serial));
        };
    }

    /// <summary>Removes the app's notification registration (Start menu shortcut-less apps keep one in the registry).</summary>
    public static void Uninstall()
    {
        try { ToastNotificationManagerCompat.Uninstall(); }
        catch (Exception) { }
    }
}
