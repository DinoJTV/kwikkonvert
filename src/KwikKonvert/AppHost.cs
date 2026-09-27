using KwikKonvert.Core.Models;
using KwikKonvert.Core.Services;
using KwikKonvert.Forms;
using KwikKonvert.Platform;

namespace KwikKonvert;

/// <summary>One file to process: which format to write, how much to compress it, and optionally a size to fit under.</summary>
public sealed record WorkItem(string Path, string Target, SqueezeLevel Squeeze, long? TargetBytes = null);

/// <summary>
/// Composition root: owns the services and the windows, keeps Explorer's menu in sync, and routes
/// command-line / Explorer requests to the right window. Everything here runs on the UI thread
/// (queue events are marshalled by the forms themselves).
/// </summary>
public sealed class AppHost : IDisposable
{
    public static string DataFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KwikKonvert");

    public ApplicationContext Context { get; } = new();
    public JsonStore<AppSettings> Settings { get; }
    public FormatService Formats { get; }
    public PreferencesService Prefs { get; }
    public HistoryService History { get; }
    public FileService Files { get; }
    public ConversionQueue Queue { get; }
    public ExplorerMenuPlanner ExplorerPlanner { get; }

    private readonly Control _invoker = new();
    private readonly System.Windows.Forms.Timer _explorerTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _zipTimer = new() { Interval = 700 };
    private readonly List<string> _zipPending = [];
    private readonly List<ProgressForm> _progressForms = [];
    private int _archivesRunning;
    private MainForm? _main;
    private bool _exiting;

    public AppHost()
    {
        _ = _invoker.Handle; // create the window handle now so Post() works from any thread
        Directory.CreateDirectory(DataFolder);

        Formats = WindowsCodecs.CreateFormatService();
        Settings = new JsonStore<AppSettings>(Path.Combine(DataFolder, "settings.json"));
        Prefs = new PreferencesService(new JsonStore<Preferences>(Path.Combine(DataFolder, "preferences.json")), Settings, Formats);
        History = new HistoryService(new JsonStore<History>(Path.Combine(DataFolder, "history.json")));
        Files = new FileService(ShellHelper.KnownFolder);
        ExplorerPlanner = new ExplorerMenuPlanner(Formats, Prefs);

        // Everything happens on this PC with Windows' own codecs. Two at a time keeps the PC responsive.
        Queue = new ConversionQueue(new LocalConverter(Formats), PlanOutputPath, maxConcurrent: 2);
        Queue.JobFinished += OnJobFinished;

        _explorerTimer.Tick += (_, _) => { _explorerTimer.Stop(); _ = RefreshExplorerAsync(); };
        _zipTimer.Tick += (_, _) => { _zipTimer.Stop(); StartPendingArchive(); };
    }

    public static long? FileSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return null; }
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread.</summary>
    public void Post(Action action)
    {
        if (_invoker.IsDisposed) return;
        try { _invoker.BeginInvoke(action); } catch (InvalidOperationException) { /* shutting down */ }
    }

    public void Start(string[] args)
    {
        StartupService.RemoveLegacyAutostart();
        Settings.Changed += (_, _) => Post(ScheduleExplorerRefresh);
        Prefs.Changed += (_, _) => Post(ScheduleExplorerRefresh);
        ScheduleExplorerRefresh();

        HandleArguments(args, fromOtherInstance: false);
        Post(MaybeExit); // e.g. Explorer passed a file that no longer exists: nothing to show
    }

    // ------------------------------------------------------------------ command routing

    /// <summary>
    ///   (no args)                    main window
    ///   file / folder …              main window with those files
    ///   --convert mp4 file …         progress window, converts straight away (Explorer)
    ///   --compress [level] file …    progress window, compresses straight away (Explorer)
    ///   --instant file …             Instant Konvert rule (Explorer)
    ///   --pick file …                main window with those files ("More formats...")
    ///   --fit 10 file …              progress window, fits each file under 10 MB (Explorer)
    ///   --zip file/folder …          one ZIP of everything selected (Explorer; one launch per item, so they're gathered)
    /// </summary>
    public void HandleArguments(string[] argv, bool fromOtherInstance)
    {
        var cmd = argv.Length > 0 ? argv[0] : "";
        switch (cmd)
        {
            case "--convert" when argv.Length >= 3:
            {
                var target = argv[1].ToLowerInvariant();
                RunInProgressWindow($"convert:{target}", ExistingFiles(argv[2..]).Select(f => new WorkItem(f, target, SqueezeLevel.None)).ToList());
                break;
            }
            case "--compress" when argv.Length >= 2:
            {
                var rest = argv[1..];
                var level = Settings.Read().DefaultSqueeze;
                if (!rest[0].Contains('\\') && Enum.TryParse<SqueezeLevel>(rest[0], ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                {
                    level = parsed;
                    rest = rest[1..];
                }
                if (level == SqueezeLevel.None) level = SqueezeLevel.Normal;
                RunInProgressWindow($"compress:{level}", ExistingFiles(rest)
                    .Select(f => new WorkItem(f, Formats.CompressTargetFor(Formats.DetectOrGeneric(f)), level)).ToList());
                break;
            }
            case "--fit" when argv.Length >= 3 && int.TryParse(argv[1], out var mb) && mb > 0:
            {
                var limit = mb * CompressionPresets.Megabyte;
                RunInProgressWindow($"fit:{mb}", ExistingFiles(argv[2..])
                    .Select(f => new WorkItem(f, Formats.CompressTargetFor(Formats.DetectOrGeneric(f)), SqueezeLevel.Normal, limit)).ToList());
                break;
            }
            case "--zip" when argv.Length >= 2:
                // Explorer starts one KwikKonvert per selected item: collect them for a moment, then make one archive.
                foreach (var p in ExistingFilesAndFolders(argv[1..]))
                    if (!_zipPending.Contains(p, StringComparer.OrdinalIgnoreCase)) _zipPending.Add(p);
                _zipTimer.Stop();
                _zipTimer.Start();
                break;
            case "--instant" when argv.Length >= 2:
                InstantKonvert(ExistingFiles(argv[1..]));
                break;
            case "--pick" when argv.Length >= 2:
                ShowMain(ExistingFiles(argv[1..]));
                break;
            case "":
            case "--show":
            case "--tray": // old autostart entry
                ShowMain();
                break;
            default:
                ShowMain(ExistingFilesAndFolders(argv));
                break;
        }
    }

    private static List<string> ExistingFiles(IEnumerable<string> args) =>
        args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a)).Select(Path.GetFullPath).ToList();

    private static List<string> ExistingFilesAndFolders(IEnumerable<string> args) =>
        args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && (File.Exists(a) || Directory.Exists(a))).Select(Path.GetFullPath).ToList();

    private void InstantKonvert(IReadOnlyList<string> files)
    {
        var settings = Settings.Read();
        var ruled = new List<WorkItem>();
        var unruled = new List<string>();
        foreach (var f in files)
        {
            var src = Formats.Detect(f);
            var rule = src is null ? null : settings.RuleFor(src.Id);
            if (rule is not null) ruled.Add(new WorkItem(f, rule.To.ToLowerInvariant(), SqueezeLevel.None));
            else unruled.Add(f);
        }
        if (ruled.Count > 0) RunInProgressWindow("instant", ruled);
        if (unruled.Count > 0) ShowMain(unruled); // the rule was removed since the menu was built: let the user choose
    }

    /// <summary>
    /// Explorer starts KwikKonvert once per selected file, so files for the same action that arrive while a
    /// progress window for that action is still busy are added to it instead of opening another window.
    /// </summary>
    private void RunInProgressWindow(string actionKey, IReadOnlyList<WorkItem> items)
    {
        if (items.Count == 0) return;
        var existing = _progressForms.FirstOrDefault(f => f.ActionKey == actionKey && !f.IsDisposed && f.AcceptsMoreFiles);
        if (existing is not null)
        {
            existing.Add(items);
            return;
        }

        var form = new ProgressForm(this, actionKey);
        _progressForms.Add(form);
        form.FormClosed += (_, _) => { _progressForms.Remove(form); Post(MaybeExit); };
        form.Show();
        form.Activate();
        form.Add(items);
    }

    private void StartPendingArchive()
    {
        if (_zipPending.Count == 0) return;
        var paths = _zipPending.ToList();
        _zipPending.Clear();

        var form = new ProgressForm(this, "zip");
        _progressForms.Add(form);
        form.FormClosed += (_, _) => { _progressForms.Remove(form); Post(MaybeExit); };
        form.Show();
        form.Activate();
        var level = Settings.Read().DefaultSqueeze;
        form.StartArchive(paths, level == SqueezeLevel.None ? SqueezeLevel.Normal : level);
    }

    /// <summary>
    /// Writes one .zip for the plan (next to the files, or in the configured output folder) and returns its path.
    /// Written to a temp name first, then moved into place without overwriting anything.
    /// </summary>
    public async Task<string> CreateArchiveAsync(ArchivePlan plan, SqueezeLevel level, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        _archivesRunning++;
        string? temp = null;
        try
        {
            var s = Settings.Read();
            var folder = s.OutputLocation == OutputLocation.SameFolder || plan.Entries.Count == 0
                ? plan.BaseFolder
                : Files.OutputFolderFor(plan.Entries[0].FullPath, s.OutputLocation, s.CustomOutputFolder);
            var desired = FileService.FreeOutputPath(folder, plan.SuggestedName, "zip", s.Collision);
            temp = Path.Combine(folder, $"{plan.SuggestedName}.kwikpart-{Guid.NewGuid():N}.zip");

            await Task.Run(() => ZipArchiver.CreateAsync(plan, temp, level, progress, ct), ct);
            var final = FileService.MoveIntoPlaceNoOverwrite(temp, desired);

            History.Add(new HistoryEntry
            {
                SourcePath = plan.Entries.Count == 1 ? plan.Entries[0].FullPath : Path.Combine(plan.BaseFolder, $"{plan.FileCount} files"),
                OutputPath = final,
                SourceFormat = "files",
                TargetFormat = "zip",
                Success = true,
                OutputBytes = FileSize(final) ?? 0,
            });
            return final;
        }
        finally
        {
            if (temp is not null) { try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ } }
            _archivesRunning--;
            Post(MaybeExit);
        }
    }

    // ------------------------------------------------------------------ windows

    public void ShowMain(IReadOnlyList<string>? files = null)
    {
        if (_main is null || _main.IsDisposed)
        {
            _main = new MainForm(this);
            _main.FormClosed += (_, _) => { _main = null; Post(MaybeExit); };
            _main.Show();
        }
        if (_main.WindowState == FormWindowState.Minimized) _main.WindowState = FormWindowState.Normal;
        _main.Activate();
        if (files is { Count: > 0 }) _main.AddPaths(files);
    }

    /// <summary>Quit once no window is open and nothing is converting.</summary>
    public void MaybeExit()
    {
        if (_exiting) return;
        if (Application.OpenForms.Count > 0 || Queue.IsBusy || _archivesRunning > 0 || _zipPending.Count > 0) return;
        _exiting = true;
        Context.ExitThread();
    }

    // ------------------------------------------------------------------ conversions

    /// <summary>
    /// Where a job's result goes: the configured folder (or the job's own), named after the original.
    /// Compressed copies get " (small)" so they never look like the original; ZIPs keep the plain name like 7-Zip does.
    /// </summary>
    private string PlanOutputPath(ConversionJob job)
    {
        var s = Settings.Read();
        var folder = job.OutputFolder is { } f && Directory.Exists(f)
            ? f
            : Files.OutputFolderFor(job.SourcePath, s.OutputLocation, s.CustomOutputFolder);
        var stem = FormatService.StripKnownExtension(Path.GetFileName(job.SourcePath));
        if (job.Squeeze != SqueezeLevel.None && job.TargetFormat != "zip") stem += " (small)";
        return FileService.FreeOutputPath(folder, stem, job.TargetFormat, s.Collision);
    }

    private void OnJobFinished(object? sender, ConversionJob job)
    {
        // Worker thread. Local bookkeeping only; nothing leaves this PC.
        if (job.Status is JobStatus.Succeeded or JobStatus.Failed)
        {
            History.Add(new HistoryEntry
            {
                SourcePath = job.SourcePath,
                OutputPath = job.Result?.OutputPath,
                SourceFormat = job.Source.Id,
                TargetFormat = job.TargetBytes is { } t ? $"{job.TargetFormat} (under {t / CompressionPresets.Megabyte} MB)"
                    : job.Squeeze == SqueezeLevel.None ? job.TargetFormat : $"{job.TargetFormat} ({job.Squeeze.ToString().ToLowerInvariant()})",
                Success = job.Status == JobStatus.Succeeded,
                Error = job.Error?.Message,
                OutputBytes = job.Result?.OutputBytes ?? 0,
            });
        }
        if (job.Status == JobStatus.Succeeded && job.Squeeze == SqueezeLevel.None && job.TargetBytes is null)
            Prefs.RecordUse(job.Source.Id, job.TargetFormat);

        Post(MaybeExit);
    }

    // ------------------------------------------------------------------ Explorer

    public void ScheduleExplorerRefresh()
    {
        _explorerTimer.Stop();
        _explorerTimer.Start();
    }

    public async Task RefreshExplorerAsync(bool force = false)
    {
        var settings = Settings.Read();
        try
        {
            var plan = ExplorerPlanner.Plan(settings); // empty when the convert menu is off
            await Task.Run(() =>
            {
                if (!settings.ExplorerEnabled && !settings.ExplorerCompress) ExplorerIntegration.RemoveAll();
                else ExplorerIntegration.Apply(plan, settings.ExplorerCompress, settings.CustomTargetMB, force);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Explorer registration failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        Queue.CancelAll();
        _explorerTimer.Dispose();
        _zipTimer.Dispose();
        _invoker.Dispose();
    }
}
