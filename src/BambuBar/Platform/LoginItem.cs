using System.IO;
using Microsoft.Win32;

namespace BambuBar.Platform;

/// <summary>"Open at login": an entry under <c>HKCU\…\CurrentVersion\Run</c> that starts the app in the tray
/// (<c>--background</c>). Windows lets people switch such entries off in Settings → Apps → Startup or Task Manager;
/// <see cref="DisabledBySystem"/> says when they did.</summary>
public sealed class LoginItem
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Bambu Bar";

    public static LoginItem Shared { get; } = new();

    public event Action? Changed;

    public string? Error { get; private set; }

    private static string Command => $"\"{Environment.ProcessPath}\" --background";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    /// <summary>Registered, but turned off in Startup apps (Settings or Task Manager).</summary>
    public bool DisabledBySystem
    {
        get
        {
            if (!IsEnabled) return false;
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // The first byte is even when enabled (2, 6) and odd when disabled (3, 7).
            return key?.GetValue(ValueName) is byte[] { Length: > 0 } state && (state[0] & 1) == 1;
        }
    }

    public void SetEnabled(bool enabled)
    {
        Error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Error = $"Couldn't change the startup entry: {e.Message}";
        }
        Changed?.Invoke();
    }

    /// <summary>Points an existing entry at this copy of the app, in case the exe was moved or updated elsewhere.</summary>
    public void Refresh()
    {
        if (!IsEnabled) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key?.GetValue(ValueName) is string current && current != Command && !Services.AppArguments.Current.IsDeveloperRun)
        {
            try { key.SetValue(ValueName, Command); }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        Changed?.Invoke();
    }

    public static void OpenStartupSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }); }
        catch (Exception) { }
    }
}
