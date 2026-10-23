using System.Runtime.InteropServices;

namespace BambuBar.Platform;

internal static partial class Native
{
    // MARK: - Popup menus (the tray icon's right-click menu)

    public const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
    public const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_BOTTOMALIGN = 0x20, TPM_RIGHTALIGN = 0x8;
    public const int WM_NULL = 0;

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string? text);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

    // Undocumented but stable since Windows 10 1903, and what Explorer itself uses: dark Win32 menus for an app
    // in dark mode. Ordinal 135 is SetPreferredAppMode, 136 FlushMenuThemes.
    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    /// <summary>Lets popup menus follow the app's light or dark mode. A no-op where Windows lacks it.</summary>
    public static void FollowDarkModeInMenus(bool dark)
    {
        if (Environment.OSVersion.Version.Build < 18362) return;
        try
        {
            SetPreferredAppMode(dark ? 2 : 3);  // ForceDark : ForceLight
            FlushMenuThemes();
        }
        catch (EntryPointNotFoundException) { }
    }
}
