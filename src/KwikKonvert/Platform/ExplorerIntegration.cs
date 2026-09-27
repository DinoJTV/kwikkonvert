using KwikKonvert.Core.Models;
using KwikKonvert.Core.Services;
using Microsoft.Win32;
using static KwikKonvert.Platform.NativeMethods;

namespace KwikKonvert.Platform;

/// <summary>
/// Writes KwikKonvert's right-click menus, per user (HKCU, no admin needed):
///
///   HKCU\Software\Classes\SystemFileAssociations\.mov\shell\KwikKonvert     "KwikKonvert" cascade
///       shell\01  "Convert to MP4"        command: "KwikKonvert.exe" --convert mp4 "%1"
///       shell\99  "More formats..."       command: --pick "%1"
///
///   HKCU\Software\Classes\*\shell\KwikKonvertCompress                   "Compress with KwikKonvert" cascade (all files)
///       shell\1   "Light - barely noticeable"   command: --compress light "%1"   …
///       shell\5   "Fit under 10 MB (Discord)"   command: --fit 10 "%1"          …
///       shell\9   "Add to one ZIP archive"      command: --zip "%1"
///   HKCU\Software\Classes\Directory\shell\KwikKonvertZip                "Add to ZIP with KwikKonvert" (folders)
///
/// With an Instant Konvert rule the top verb runs straight away (--instant "%1") and the cascade becomes "KwikKonvert as".
/// On Windows 11 these classic verbs are under "Show more options" (Shift+F10).
/// </summary>
public static class ExplorerIntegration
{
    private const string Root = @"Software\Classes\SystemFileAssociations";
    private const string AllFilesShell = @"Software\Classes\*\shell";
    private const string FolderShell = @"Software\Classes\Directory\shell";
    private const string ZipVerb = "KwikKonvertZip";
    private const string Verb = "KwikKonvert";
    private const string VerbAs = "KwikKonvertAs";
    private const string CompressVerb = "KwikKonvertCompress";
    private const string StateKey = @"Software\KwikKonvert";
    private const int ECF_SEPARATORBEFORE = 0x20;

    /// <summary>Bump when the registry layout or labels change so existing installs get rewritten.</summary>
    private const string LayoutVersion = "win32-v2";

    private static readonly object Gate = new();

    /// <summary>Applies the plan if it differs from what is registered. Safe to call often; call it off the UI thread.</summary>
    public static void Apply(IReadOnlyList<ExplorerExtensionMenu> plan, bool compressMenu, int customTargetMB, bool force = false)
    {
        lock (Gate)
        {
            var exe = ShellHelper.ExePath;
            var fingerprint = $"{LayoutVersion}|{compressMenu}|{customTargetMB}|{ExplorerMenuPlanner.Fingerprint(plan, exe)}";
            using var state = Registry.CurrentUser.CreateSubKey(StateKey, writable: true);
            if (!force && state.GetValue("ExplorerPlan") as string == fingerprint) return;

            var previous = (state.GetValue("ExplorerExtensions") as string[]) ?? [];
            var current = plan.Select(p => p.Extension).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var ext in previous.Where(e => !current.Contains(e)))
                RemoveFor(ext);
            foreach (var menu in plan)
                WriteFor(menu, exe);

            if (compressMenu) WriteCompressMenu(exe, customTargetMB);
            else RemoveCompressMenu();

            state.SetValue("ExplorerExtensions", current.ToArray(), RegistryValueKind.MultiString);
            state.SetValue("ExplorerPlan", fingerprint);
        }

        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Removes every menu KwikKonvert ever registered (when disabled, and for --unregister).</summary>
    public static void RemoveAll()
    {
        lock (Gate)
        {
            using var state = Registry.CurrentUser.CreateSubKey(StateKey, writable: true);
            foreach (var ext in (state.GetValue("ExplorerExtensions") as string[]) ?? [])
                RemoveFor(ext);
            RemoveCompressMenu();
            state.DeleteValue("ExplorerExtensions", throwOnMissingValue: false);
            state.DeleteValue("ExplorerPlan", throwOnMissingValue: false);
        }
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    private static void RemoveFor(string ext)
    {
        using var shell = Registry.CurrentUser.OpenSubKey($@"{Root}\{ext}\shell", writable: true);
        if (shell is null) return;
        shell.DeleteSubKeyTree(Verb, throwOnMissingSubKey: false);
        shell.DeleteSubKeyTree(VerbAs, throwOnMissingSubKey: false);
    }

    private static void RemoveCompressMenu()
    {
        using (var shell = Registry.CurrentUser.OpenSubKey(AllFilesShell, writable: true))
            shell?.DeleteSubKeyTree(CompressVerb, throwOnMissingSubKey: false);
        using (var folders = Registry.CurrentUser.OpenSubKey(FolderShell, writable: true))
            folders?.DeleteSubKeyTree(ZipVerb, throwOnMissingSubKey: false);
    }

    private static void WriteCompressMenu(string exe, int customTargetMB)
    {
        RemoveCompressMenu();
        using var shell = Registry.CurrentUser.CreateSubKey(AllFilesShell, writable: true);
        using var v = shell.CreateSubKey(CompressVerb, writable: true);
        v.SetValue("MUIVerb", "Compress with KwikKonvert");
        v.SetValue("Icon", $"\"{exe}\",0");
        v.SetValue("MultiSelectModel", "Player");
        v.SetValue("SubCommands", "");
        using var sub = v.CreateSubKey("shell", writable: true);
        var n = 1;
        void Item(string label, string args, bool separatorBefore = false)
        {
            using var k = sub.CreateSubKey($"{n++:D2}", writable: true);
            k.SetValue("MUIVerb", label);
            if (separatorBefore) k.SetValue("CommandFlags", ECF_SEPARATORBEFORE, RegistryValueKind.DWord);
            using var c = k.CreateSubKey("command", writable: true);
            c.SetValue("", $"\"{exe}\" {args} \"%1\"");
        }

        foreach (var level in new[] { SqueezeLevel.Light, SqueezeLevel.Normal, SqueezeLevel.Strong, SqueezeLevel.Maximum })
            Item(CompressionPresets.Describe(level), $"--compress {level.ToString().ToLowerInvariant()}");

        var first = true;
        foreach (var (mb, label) in CompressionPresets.TargetPresets)
        {
            Item(label, $"--fit {mb}", separatorBefore: first);
            first = false;
        }
        if (customTargetMB > 0 && CompressionPresets.TargetPresets.All(t => t.MB != customTargetMB))
            Item($"Fit under {customTargetMB} MB", $"--fit {customTargetMB}");

        Item("Add to one ZIP archive", "--zip", separatorBefore: true);

        // Folders: straight "Add to ZIP".
        using var folders = Registry.CurrentUser.CreateSubKey(FolderShell, writable: true);
        using var z = folders.CreateSubKey(ZipVerb, writable: true);
        z.SetValue("MUIVerb", "Add to ZIP with KwikKonvert");
        z.SetValue("Icon", $"\"{exe}\",0");
        z.SetValue("MultiSelectModel", "Player");
        using var zc = z.CreateSubKey("command", writable: true);
        zc.SetValue("", $"\"{exe}\" --zip \"%1\"");
    }

    private static void WriteFor(ExplorerExtensionMenu menu, string exe)
    {
        RemoveFor(menu.Extension);
        using var shell = Registry.CurrentUser.CreateSubKey($@"{Root}\{menu.Extension}\shell", writable: true);
        var icon = $"\"{exe}\",0";

        if (menu.InstantTarget is { } instant)
        {
            using var v = shell.CreateSubKey(Verb, writable: true);
            v.SetValue("MUIVerb", $"KwikKonvert to {instant.ToUpperInvariant()}");
            v.SetValue("Icon", icon);
            v.SetValue("MultiSelectModel", "Player");
            using var cmd = v.CreateSubKey("command", writable: true);
            cmd.SetValue("", $"\"{exe}\" --instant \"%1\"");

            WriteCascade(shell, VerbAs, "KwikKonvert as", menu, exe, icon);
        }
        else
        {
            WriteCascade(shell, Verb, "KwikKonvert", menu, exe, icon);
        }
    }

    private static void WriteCascade(RegistryKey shell, string verbName, string label, ExplorerExtensionMenu menu, string exe, string icon)
    {
        using var v = shell.CreateSubKey(verbName, writable: true);
        v.SetValue("MUIVerb", label);
        v.SetValue("Icon", icon);
        v.SetValue("MultiSelectModel", "Player");
        v.SetValue("SubCommands", ""); // empty = use the nested "shell" key below

        using var sub = v.CreateSubKey("shell", writable: true);
        var n = 1;
        foreach (var item in menu.QuickItems)
        {
            using var k = sub.CreateSubKey($"{n++:D2}", writable: true);
            k.SetValue("MUIVerb", $"Convert to {item.Format.ToUpperInvariant()}");
            using var c = k.CreateSubKey("command", writable: true);
            c.SetValue("", $"\"{exe}\" --convert {item.Format} \"%1\"");
        }

        if (menu.ShowMore)
        {
            using var more = sub.CreateSubKey("99", writable: true);
            more.SetValue("MUIVerb", "More formats...");
            if (menu.QuickItems.Count > 0) more.SetValue("CommandFlags", ECF_SEPARATORBEFORE, RegistryValueKind.DWord);
            using var c = more.CreateSubKey("command", writable: true);
            c.SetValue("", $"\"{exe}\" --pick \"%1\"");
        }
    }
}
