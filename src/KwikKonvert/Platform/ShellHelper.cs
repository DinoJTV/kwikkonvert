using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using KwikKonvert.Core.Services;
using static KwikKonvert.Platform.NativeMethods;

namespace KwikKonvert.Platform;

/// <summary>Opening files, revealing them in Explorer, known folders, embedded assets.</summary>
public static class ShellHelper
{
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "KwikKonvert.exe");

    /// <summary>Assets are embedded in the exe (so the single-file build needs nothing next to it).</summary>
    public static Stream? OpenAsset(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream("KwikKonvert.Assets." + name);

    public static string? KnownFolder(OutputLocation location) => location switch
    {
        OutputLocation.Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        OutputLocation.Downloads => GetDownloads(),
        _ => null,
    };

    public static string GetDownloads()
    {
        if (SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out var p) == 0)
        {
            try { return Marshal.PtrToStringUni(p)!; }
            finally { CoTaskMemFree(p); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public static void Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The file is no longer there.", path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void Reveal(string path)
    {
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        else
            throw new FileNotFoundException("The file and its folder are no longer there.", path);
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
