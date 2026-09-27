using Microsoft.Win32;

namespace KwikKonvert.Platform;

/// <summary>Older KwikKonvert versions could start with Windows (tray icon). This version has no tray, so clean that up.</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "KwikKonvert";

    public static void RemoveLegacyAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
        }
        catch { /* not important */ }
    }
}
