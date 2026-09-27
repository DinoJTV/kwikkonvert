using System.Runtime.InteropServices;

namespace KwikKonvert.Platform;

/// <summary>
/// Progress in the window's taskbar button (green bar, like 7-Zip / Explorer copies), via ITaskbarList3.
/// Purely cosmetic: any failure (e.g. Explorer restarting) is ignored.
/// </summary>
internal static class TaskbarProgress
{
    public enum State { None = 0, Indeterminate = 0x1, Normal = 0x2, Error = 0x4, Paused = 0x8 }

    private static ITaskbarList3? _taskbar;
    private static bool _failed;

    private static ITaskbarList3? Taskbar
    {
        get
        {
            if (_taskbar is not null || _failed) return _taskbar;
            try
            {
                var tb = (ITaskbarList3)new CTaskbarList();
                tb.HrInit();
                _taskbar = tb;
            }
            catch
            {
                _failed = true;
            }
            return _taskbar;
        }
    }

    /// <summary>Shows <paramref name="percent"/> (0–100), or a moving bar when it's null.</summary>
    public static void Set(IWin32Window window, int? percent)
    {
        try
        {
            var tb = Taskbar;
            if (tb is null) return;
            if (percent is { } p)
            {
                tb.SetProgressState(window.Handle, State.Normal);
                tb.SetProgressValue(window.Handle, (ulong)Math.Clamp(p, 0, 100), 100);
            }
            else
            {
                tb.SetProgressState(window.Handle, State.Indeterminate);
            }
        }
        catch { /* cosmetic */ }
    }

    /// <summary>Red full bar: something failed. Cleared by the next <see cref="Set"/> or <see cref="Clear"/>.</summary>
    public static void Error(IWin32Window window)
    {
        try
        {
            var tb = Taskbar;
            if (tb is null) return;
            tb.SetProgressState(window.Handle, State.Error);
            tb.SetProgressValue(window.Handle, 100, 100);
        }
        catch { /* cosmetic */ }
    }

    public static void Clear(IWin32Window window)
    {
        try { Taskbar?.SetProgressState(window.Handle, State.None); }
        catch { /* cosmetic */ }
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class CTaskbarList { }

    // Methods must stay in vtable order: ITaskbarList, ITaskbarList2, then ITaskbarList3 (only up to what we use).
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, State state);
    }
}
