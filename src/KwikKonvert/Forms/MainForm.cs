using KwikKonvert.Core.Models;
using KwikKonvert.Core.Services;
using KwikKonvert.Platform;

namespace KwikKonvert.Forms;

/// <summary>
/// The main window: a plain file list with Format / Compression / Output folder underneath, like a small
/// Windows utility. Files come in by drag and drop, File > Add, Paste, or Explorer.
/// </summary>
internal sealed class MainForm : Form
{
    private const int MaxFilesFromFolder = 10_000;

    private readonly AppHost _app;
    private readonly List<Entry> _entries = [];
    private readonly MessageRotator _rotator = new();

    private readonly ListView _list = new();
    private readonly ComboBox _format = new();
    private readonly ComboBox _level = new();
    private readonly ComboBox _output = new();
    private readonly Label _hint = new();
    private readonly ProgressBar _progress = new();
    private readonly DogeBox _doge = Ui.MakeDoge(96);
    private readonly Button _start;
    private readonly Button _stop;
    private readonly ToolStripStatusLabel _statusText = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _statusCount = new() { BorderSides = ToolStripStatusLabelBorderSides.Left };
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 250 };
    private readonly ContextMenuStrip _itemMenu = new();

    private bool _running;
    private DateTime _startedAt;
    private int _lastLevelIndex;
    private CancellationTokenSource? _archiveCts;
    private ArchiveProgress? _archiveProgress;
    private string? _lastArchive;
    private bool _suppressEvents;
    private DateTime _nextMessageAt;
    private string _flavour = "";

    private sealed class Entry
    {
        public required string Path { get; init; }
        public required FormatInfo Format { get; init; }
        public required long Size { get; init; }
        public required ListViewItem Item { get; init; }
        public ConversionJob? Job { get; set; }
        public string? Note { get; set; }
    }

    public MainForm(AppHost app)
    {
        _app = app;
        _start = Ui.MakeButton("&Start", (_, _) => StartJobs());
        _stop = Ui.MakeButton("S&top", (_, _) => StopJobs());

        SuspendLayout();
        Ui.Prepare(this, "KwikKonvert");
        ClientSize = new Size(760, 520);
        MinimumSize = new Size(560, 420);

        var menu = BuildMenu();
        var status = new StatusStrip { SizingGrip = true };
        status.Items.AddRange(new ToolStripItem[] { _statusText, _statusCount });

        BuildList();
        var bottom = BuildOptions();

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(6) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_list, 0, 0);
        root.Controls.Add(bottom, 0, 1);

        Controls.Add(root);
        Controls.Add(status);
        Controls.Add(menu);
        MainMenuStrip = menu;
        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;

        _tick.Tick += (_, _) => RefreshJobs();
        FormClosing += OnClosing;
        FormClosed += (_, _) => { _tick.Dispose(); };

        ResumeLayout(true);

        LoadChoices();
        UpdateState();
    }

    // ------------------------------------------------------------------ layout

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&Add files...", null, (_, _) => BrowseFiles(), Keys.Control | Keys.O));
        file.DropDownItems.Add(new ToolStripMenuItem("Add &folder...", null, (_, _) => BrowseFolder(), Keys.Control | Keys.Shift | Keys.O));
        file.DropDownItems.Add(new ToolStripMenuItem("&Paste files", null, (_, _) => PasteFiles(), Keys.Control | Keys.V));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("&Remove selected", null, (_, _) => RemoveSelected()) { ShortcutKeyDisplayString = "Del" });
        file.DropDownItems.Add(new ToolStripMenuItem("&Clear list", null, (_, _) => ClearList()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close()));

        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add(new ToolStripMenuItem("&Options...", null, (_, _) => ShowOptions()));
        tools.DropDownItems.Add(new ToolStripMenuItem("&History...", null, (_, _) => { using var f = new HistoryForm(_app); f.ShowDialog(this); }, Keys.Control | Keys.H));
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(new ToolStripMenuItem("&Refresh Explorer menu", null, async (_, _) =>
        {
            await _app.RefreshExplorerAsync(force: true);
            SetStatus("Explorer right-click menu refreshed.");
        }));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("&Formats on this PC...", null, (_, _) => { using var f = new FormatsForm(_app); f.ShowDialog(this); }));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(new ToolStripMenuItem("&About KwikKonvert", null, (_, _) => { using var f = new AboutForm(); f.ShowDialog(this); }));

        menu.Items.AddRange(new ToolStripItem[] { file, tools, help });
        return menu;
    }

    private void BuildList()
    {
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.AllowDrop = true;
        _list.Margin = new Padding(0, 0, 0, 6);
        _list.Columns.Add("Name", 230);
        _list.Columns.Add("Size", 80, HorizontalAlignment.Right);
        _list.Columns.Add("Type", 60);
        _list.Columns.Add("Status", 150);
        _list.Columns.Add("Result", 220);
        Ui.EnableDoubleBuffer(_list);

        _list.DragEnter += OnDragEnter;
        _list.DragDrop += OnDragDrop;
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.A && e.Control) { foreach (ListViewItem i in _list.Items) i.Selected = true; e.Handled = true; }
        };
        _list.ItemActivate += (_, _) => OpenSelected(reveal: false);

        var openResult = new ToolStripMenuItem("&Open", null, (_, _) => OpenSelected(reveal: false));
        var reveal = new ToolStripMenuItem("Show in &folder", null, (_, _) => OpenSelected(reveal: true));
        var openOriginal = new ToolStripMenuItem("Open o&riginal", null, (_, _) =>
        {
            if (Selected().FirstOrDefault() is { } e) Ui.TryOpen(this, e.Path, reveal: false);
        });
        var remove = new ToolStripMenuItem("&Remove", null, (_, _) => RemoveSelected());
        openResult.Font = new Font(openResult.Font, FontStyle.Bold);
        _itemMenu.Items.AddRange(new ToolStripItem[] { openResult, reveal, openOriginal, new ToolStripSeparator(), remove });
        _itemMenu.Opening += (_, e) =>
        {
            var sel = Selected().ToList();
            if (sel.Count == 0) { e.Cancel = true; return; }
            remove.Enabled = sel.All(s => s.Job is not { IsFinished: false });
        };
        _list.ContextMenuStrip = _itemMenu;
    }

    private Control BuildOptions()
    {
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2, Margin = new Padding(0) };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var group = new GroupBox { Text = "Options", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(6, 3, 6, 6) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        foreach (var combo in new[] { _format, _level, _output })
        {
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            combo.Margin = new Padding(3, 3, 3, 3);
        }
        _format.MaxDropDownItems = 20;
        _format.SelectedIndexChanged += (_, _) => OnFormatChanged();
        _level.SelectedIndexChanged += (_, _) => OnLevelChanged();
        _output.SelectedIndexChanged += (_, _) => OnOutputChanged();

        var browse = new Button { Text = "...", Width = 32, Height = _output.Height + 2, UseVisualStyleBackColor = true, Margin = new Padding(3, 2, 3, 2) };
        browse.Click += (_, _) => BrowseOutputFolder();

        // Fixed two-line height; the text wraps to the window width instead of widening it.
        _hint.AutoSize = false;
        _hint.Dock = DockStyle.Fill;
        _hint.Height = 34;
        _hint.ForeColor = SystemColors.GrayText;
        _hint.Margin = new Padding(3, 0, 3, 4);

        _progress.Dock = DockStyle.Fill;
        _progress.Height = 18;
        _progress.Margin = new Padding(3, 6, 3, 3);

        grid.Controls.Add(Ui.MakeLabel("&Format:"), 0, 0);
        grid.Controls.Add(_format, 1, 0);
        grid.Controls.Add(Ui.MakeLabel("&Compression:"), 0, 1);
        grid.Controls.Add(_level, 1, 1);
        grid.Controls.Add(_hint, 1, 2);
        grid.SetColumnSpan(_hint, 2);
        grid.Controls.Add(Ui.MakeLabel("&Output folder:"), 0, 3);
        grid.Controls.Add(_output, 1, 3);
        grid.Controls.Add(browse, 2, 3);
        grid.Controls.Add(_progress, 0, 4);
        grid.SetColumnSpan(_progress, 3);
        group.Controls.Add(grid);

        _doge.Anchor = AnchorStyles.Top;
        _doge.Margin = new Padding(8, 8, 2, 3);
        _doge.Cursor = Cursors.Hand;
        _doge.Click += (_, _) =>
        {
            if (_app.Settings.Read().EasterEggs) SetStatus(_running ? DogeMessages.PatDoge : DogeMessages.Tagline);
        };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 0),
            WrapContents = false,
        };
        var close = Ui.MakeButton("Close", (_, _) => Close());
        buttons.Controls.AddRange([close, _stop, _start]);

        outer.Controls.Add(group, 0, 0);
        outer.Controls.Add(_doge, 1, 0);
        outer.Controls.Add(buttons, 0, 1);
        outer.SetColumnSpan(buttons, 2);

        return outer;
    }

    // ------------------------------------------------------------------ choices

    /// <summary>Format-box value meaning "everything into one .zip".</summary>
    private const string ZipAll = "*zip-all*";

    private void LoadChoices()
    {
        _suppressEvents = true;
        try
        {
            var s = _app.Settings.Read();
            RefreshLevelChoices(s);
            _level.SelectedIndex = 0;
            _lastLevelIndex = 0;
            RefreshOutputChoices(s);
            RefreshFormatChoices();
        }
        finally
        {
            _suppressEvents = false;
        }
        UpdateHint();
    }

    /// <summary>Quality levels, then "fit under" sizes (presets + the user's own).</summary>
    private void RefreshLevelChoices(AppSettings s)
    {
        var keep = _level.SelectedIndex;
        _level.BeginUpdate();
        _level.Items.Clear();
        foreach (var level in Enum.GetValues<SqueezeLevel>())
            _level.Items.Add(new Choice<CompressionChoice>(new CompressionChoice(level), CompressionPresets.Describe(level)));
        foreach (var (mb, label) in CompressionPresets.TargetPresets)
            _level.Items.Add(new Choice<CompressionChoice>(new CompressionChoice(SqueezeLevel.Normal, mb * CompressionPresets.Megabyte), label));
        _level.Items.Add(new Choice<CompressionChoice>(
            new CompressionChoice(SqueezeLevel.Normal, s.CustomTargetMB * CompressionPresets.Megabyte, IsCustom: true),
            $"Fit under {s.CustomTargetMB} MB (custom size)..."));
        _level.EndUpdate();
        if (keep >= 0 && keep < _level.Items.Count) _level.SelectedIndex = keep;
    }

    private void RefreshOutputChoices(AppSettings s)
    {
        _output.Items.Clear();
        _output.Items.Add(new Choice<OutputLocation>(OutputLocation.SameFolder, "Same folder as the original file"));
        _output.Items.Add(new Choice<OutputLocation>(OutputLocation.Desktop, "Desktop"));
        _output.Items.Add(new Choice<OutputLocation>(OutputLocation.Downloads, "Downloads"));
        if (!string.IsNullOrWhiteSpace(s.CustomOutputFolder))
            _output.Items.Add(new Choice<OutputLocation>(OutputLocation.Custom, s.CustomOutputFolder));

        var index = s.OutputLocation switch
        {
            OutputLocation.Desktop => 1,
            OutputLocation.Downloads => 2,
            OutputLocation.Custom when _output.Items.Count > 3 => 3,
            _ => 0,
        };
        _output.SelectedIndex = index;
    }

    /// <summary>
    /// "Automatic" first, then the formats every listed file can become (ordered by habit / favourites / popularity),
    /// then "One ZIP archive".
    /// </summary>
    private void RefreshFormatChoices()
    {
        var previous = (_format.SelectedItem as Choice<string?>)?.Value;
        var hadSelection = _format.SelectedIndex >= 0;

        var sources = _entries.Select(e => e.Format).ToList();
        IEnumerable<FormatInfo> targets = sources.Count == 0 ? _app.Formats.Writable : _app.Formats.CommonTargets(sources);
        var ordered = _app.Prefs.Order(sources.Count > 0 && sources.All(s => s.Id == sources[0].Id) ? sources[0] : null, targets);

        _suppressEvents = true;
        try
        {
            _format.BeginUpdate();
            _format.Items.Clear();
            _format.Items.Add(new Choice<string?>(null, "Automatic - smallest sensible format for each file"));
            foreach (var c in ordered)
                _format.Items.Add(new Choice<string?>(c.Format.Id, $"{c.Format.Label} - {c.Format.Description}{(c.IsFavourite ? "  (favourite)" : "")}"));
            _format.Items.Add(new Choice<string?>(ZipAll, "One ZIP archive - all files together"));
            _format.EndUpdate();

            var keep = hadSelection ? _format.Items.Cast<Choice<string?>>().ToList().FindIndex(c => c.Value == previous) : -1;
            // Default: the most likely format (1), or Automatic when the files have nothing in common.
            _format.SelectedIndex = keep >= 0 ? keep : ordered.Count > 0 ? 1 : 0;
            if (SelectedFormat is null && !SelectedCompression.Shrinks && _level.Items.Count > 0)
                SelectLevel(DefaultCompressLevel());
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private string? SelectedFormat => (_format.SelectedItem as Choice<string?>)?.Value;
    private bool ZipAllSelected => SelectedFormat == ZipAll;
    private CompressionChoice SelectedCompression => (_level.SelectedItem as Choice<CompressionChoice>)?.Value ?? new CompressionChoice(SqueezeLevel.None);

    private void OnFormatChanged()
    {
        if (_suppressEvents) return;
        // Automatic only makes sense when shrinking: pick the default level if none was chosen.
        if (SelectedFormat is null && !SelectedCompression.Shrinks)
        {
            _suppressEvents = true;
            try { SelectLevel(DefaultCompressLevel()); _lastLevelIndex = _level.SelectedIndex; }
            finally { _suppressEvents = false; }
        }
        UpdateHint();
        UpdateState();
    }

    private SqueezeLevel DefaultCompressLevel()
    {
        var level = _app.Settings.Read().DefaultSqueeze;
        return level == SqueezeLevel.None ? SqueezeLevel.Normal : level;
    }

    private void OnLevelChanged()
    {
        if (_suppressEvents) return;
        if (SelectedCompression.IsCustom && !AskCustomSize())
        {
            // Cancelled: go back to what was selected before.
            _suppressEvents = true;
            try { _level.SelectedIndex = _lastLevelIndex; }
            finally { _suppressEvents = false; }
        }
        _lastLevelIndex = _level.SelectedIndex;
        UpdateHint();
        UpdateState();
    }

    /// <summary>Asks for the custom "fit under" size; updates the list item and the setting.</summary>
    private bool AskCustomSize()
    {
        var s = _app.Settings.Read();
        using var dlg = new SizePromptForm(s.CustomTargetMB);
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;
        _app.Settings.Update(x => x.CustomTargetMB = dlg.Megabytes);
        _suppressEvents = true;
        try { RefreshLevelChoices(_app.Settings.Read()); }
        finally { _suppressEvents = false; }
        return true;
    }

    private void SelectLevel(SqueezeLevel level)
    {
        for (var i = 0; i < _level.Items.Count; i++)
            if (_level.Items[i] is Choice<CompressionChoice> c && c.Value.TargetBytes is null && c.Value.Level == level) { _level.SelectedIndex = i; return; }
    }

    private void UpdateHint()
    {
        var c = SelectedCompression;
        string hint;
        if (ZipAllSelected)
            hint = "Every file goes into one .zip, keeping its folders (like 7-Zip's \"Add to archive\"). " +
                   (c.Level >= SqueezeLevel.Strong || c.TargetBytes is not null ? "Smallest ZIP setting." : "Normal ZIP setting; Strong or Maximum squeezes harder.");
        else if (c.TargetBytes is { } bytes)
            hint = Ui.TargetHint(bytes);
        else if (SelectedFormat is null)
            hint = !c.Shrinks ? "Automatic needs a compression level." : "Videos become MP4, audio MP3/M4A, pictures JPG, anything else ZIP. " + Ui.LevelHint(c.Level);
        else if (_entries.Count > 0 && _format.Items.Count == 2)
            hint = "These files have no format in common. Use Automatic or One ZIP archive, or add files of one kind.";
        else
            hint = Ui.LevelHint(c.Level);
        _hint.Text = hint;
    }

    private void OnOutputChanged()
    {
        if (_suppressEvents || _output.SelectedItem is not Choice<OutputLocation> c) return;
        _app.Settings.Update(s => s.OutputLocation = c.Value);
    }

    private void BrowseOutputFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Where should KwikKonvert save the new files?",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = _app.Settings.Read().CustomOutputFolder ?? "",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _app.Settings.Update(s =>
        {
            s.CustomOutputFolder = dlg.SelectedPath;
            s.OutputLocation = OutputLocation.Custom;
        });
        _suppressEvents = true;
        try { RefreshOutputChoices(_app.Settings.Read()); }
        finally { _suppressEvents = false; }
    }

    // ------------------------------------------------------------------ adding / removing files

    private void BrowseFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Add files",
            Multiselect = true,
            CheckFileExists = true,
            Filter = BuildFilter(),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(dlg.FileNames);
    }

    private string BuildFilter()
    {
        static string Pattern(IEnumerable<FormatInfo> fs) => string.Join(";", fs.Select(f => "*." + f.Id));
        var readable = _app.Formats.Readable.Where(f => !f.Id.Contains('.')).ToList();
        var parts = new List<string> { "All files (*.*)|*.*" };
        foreach (var (cat, name) in new[] { (FormatCategory.Video, "Videos"), (FormatCategory.Audio, "Audio"), (FormatCategory.Image, "Pictures") })
        {
            var list = readable.Where(f => f.Category == cat).ToList();
            if (list.Count > 0) parts.Add($"{name}|{Pattern(list)}");
        }
        return string.Join("|", parts);
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Add every file in a folder (including subfolders)", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths([dlg.SelectedPath]);
    }

    private void PasteFiles()
    {
        if (ActiveControl is ComboBox) return;
        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList().Cast<string>().ToList();
            AddPaths(files);
        }
        else
        {
            SetStatus("The clipboard has no files. Copy files in Explorer first.");
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true && !_running ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
    }

    /// <summary>Adds files, and every file inside folders. Duplicates are ignored.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        if (_running)
        {
            SetStatus("Wait for the current batch to finish before adding more files.");
            return;
        }

        var known = _entries.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        var truncated = false;
        foreach (var p in paths)
        {
            if (File.Exists(p)) files.Add(Path.GetFullPath(p));
            else if (Directory.Exists(p))
            {
                try
                {
                    var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
                    foreach (var f in Directory.EnumerateFiles(p, "*", opts))
                    {
                        if (files.Count >= MaxFilesFromFolder) { truncated = true; break; }
                        files.Add(f);
                    }
                }
                catch (Exception ex) { SetStatus("Couldn't read the folder: " + ex.Message); }
            }
        }

        var added = 0;
        _list.BeginUpdate();
        try
        {
            foreach (var f in files)
            {
                if (!known.Add(f)) continue;
                long size;
                try { size = new FileInfo(f).Length; } catch { continue; }
                var format = _app.Formats.DetectOrGeneric(f);
                var item = new ListViewItem([Path.GetFileName(f), FileService.HumanSize(size), format.Label, "Ready", ""]) { ToolTipText = f };
                var entry = new Entry { Path = f, Format = format, Size = size, Item = item };
                item.Tag = entry;
                _entries.Add(entry);
                _list.Items.Add(item);
                added++;
            }
        }
        finally
        {
            _list.EndUpdate();
        }
        _list.ShowItemToolTips = true;

        RefreshFormatChoices();
        UpdateHint();
        UpdateState();
        if (truncated) SetStatus($"Only the first {MaxFilesFromFolder:N0} files were added.");
        else if (added > 0) SetStatus($"Added {added} file{(added == 1 ? "" : "s")}.");
    }

    private IEnumerable<Entry> Selected() => _list.SelectedItems.Cast<ListViewItem>().Select(i => (Entry)i.Tag!);

    private void RemoveSelected()
    {
        if (_archiveCts is not null) return;
        var removable = Selected().Where(e => e.Job is not { IsFinished: false }).ToList();
        if (removable.Count == 0) return;
        _list.BeginUpdate();
        foreach (var e in removable)
        {
            _entries.Remove(e);
            _list.Items.Remove(e.Item);
        }
        _list.EndUpdate();
        RefreshFormatChoices();
        UpdateHint();
        UpdateState();
    }

    private void ClearList()
    {
        if (_running) return;
        _entries.Clear();
        _list.Items.Clear();
        _app.Queue.ClearFinished();
        RefreshFormatChoices();
        UpdateHint();
        UpdateState();
        Ui.ResetProgress(_progress);
        SetStatus("");
    }

    private void OpenSelected(bool reveal)
    {
        if (Selected().FirstOrDefault() is not { } e) return;
        var path = e.Job?.Result?.OutputPath ?? (e.Item.SubItems[3].Text == "Added" ? _lastArchive : null);
        if (path is null)
        {
            if (reveal) Ui.TryOpen(this, e.Path, reveal: true);
            else SetStatus("This file hasn't been converted yet. Right-click > Open original to open it.");
            return;
        }
        Ui.TryOpen(this, path, reveal);
    }

    // ------------------------------------------------------------------ running

    private bool CanStart(out string? reason)
    {
        reason = null;
        if (_running) return false;
        if (_entries.Count == 0) { reason = "Drag files here, or use File > Add files."; return false; }
        if (SelectedFormat is null && !SelectedCompression.Shrinks) { reason = "Automatic needs a compression level."; return false; }
        return true;
    }

    private bool CanMake(FormatInfo source, string target) =>
        FormatService.AreEquivalent(source.Id, target) ||
        _app.Formats.TargetsFor(source).Any(t => FormatService.AreEquivalent(t.Id, target));

    private void BeginRun()
    {
        _running = true;
        _startedAt = DateTime.Now;
        _nextMessageAt = DateTime.MinValue;
        Ui.SetDancing(_doge, true);
        UpdateState();
    }

    private void StartJobs()
    {
        if (!CanStart(out var reason))
        {
            if (reason is not null) SetStatus(reason);
            return;
        }
        if (ZipAllSelected)
        {
            StartArchive();
            return;
        }

        var format = SelectedFormat;
        var compression = SelectedCompression;

        // Run everything that hasn't succeeded yet; if everything already has, the user wants it again.
        var todo = _entries.Where(e => e.Job?.Status != JobStatus.Succeeded).ToList();
        if (todo.Count == 0) todo = _entries.ToList();

        var chonky = todo.Any(e => e.Size >= FileService.ChonkyBytes && e.Format.Category == FormatCategory.Video);
        if (chonky && _app.Settings.Read().EasterEggs)
            SetStatus(DogeMessages.Chonky + ". this may take a while.");

        _app.Queue.ClearFinished();
        var started = 0;
        foreach (var e in todo)
        {
            var target = format ?? _app.Formats.CompressTargetFor(e.Format);
            e.Job = null;
            e.Note = null;
            if (compression.TargetBytes is { } limit && format is null && e.Size <= limit)
            {
                e.Note = $"{FileService.HumanSize(e.Size)} - left as it is";
                SetCell(e.Item, 3, "Already small enough");
                SetCell(e.Item, 4, e.Note);
                continue;
            }
            if (!CanMake(e.Format, target))
            {
                e.Note = $"Can't make {target.ToUpperInvariant()} from {e.Format.Label}";
                SetCell(e.Item, 3, "Skipped");
                SetCell(e.Item, 4, e.Note);
                continue;
            }
            e.Job = _app.Queue.Enqueue(e.Path, e.Format, target, outputFolder: null, deleteSourceWhenDone: false,
                squeeze: compression.QueueLevel, targetBytes: compression.TargetBytes);
            started++;
        }

        if (started == 0)
        {
            SetStatus(compression.TargetBytes is not null && format is null
                ? "Nothing to do: every file is already small enough."
                : "Nothing to do: none of these files can become that format.");
            return;
        }

        BeginRun();
        RefreshJobs();
        _tick.Start();
    }

    /// <summary>Everything in the list into one .zip.</summary>
    private async void StartArchive()
    {
        var plan = ZipArchiver.Plan(_entries.Select(e => e.Path));
        if (plan.Entries.Count == 0)
        {
            SetStatus("Nothing to add: the files are gone.");
            return;
        }
        var c = SelectedCompression;
        var level = c.TargetBytes is not null ? SqueezeLevel.Maximum : c.Level;

        _archiveCts = new CancellationTokenSource();
        _archiveProgress = null;
        foreach (var e in _entries) { e.Job = null; SetCell(e.Item, 3, "Waiting"); SetCell(e.Item, 4, ""); }
        BeginRun();
        _tick.Start();

        try
        {
            var progress = new Progress<ArchiveProgress>(p => _archiveProgress = p);
            var zip = await _app.CreateArchiveAsync(plan, level, progress, _archiveCts.Token);
            var size = AppHost.FileSize(zip) ?? 0;
            foreach (var e in _entries) { SetCell(e.Item, 3, "Added"); SetCell(e.Item, 4, "in " + Path.GetFileName(zip)); }
            _lastArchive = zip;
            var over = c.TargetBytes is { } t && size > t ? $"   Note: that's over {t / CompressionPresets.Megabyte} MB; ZIP can't shrink these files further." : "";
            EndRun($"Created {Path.GetFileName(zip)}.   {CompressionPresets.SizeChange(plan.TotalBytes, size)}{over}", failed: false);
        }
        catch (OperationCanceledException)
        {
            foreach (var e in _entries) SetCell(e.Item, 3, "Cancelled");
            EndRun("Cancelled. No archive was made.", failed: false);
        }
        catch (Exception ex)
        {
            foreach (var e in _entries) SetCell(e.Item, 3, "Failed");
            EndRun("Couldn't create the archive: " + Ui.FirstLine(DogeMessages.Explain(ex)), failed: true);
        }
        finally
        {
            _archiveCts?.Dispose();
            _archiveCts = null;
        }
    }

    private void StopJobs()
    {
        try { _archiveCts?.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var e in _entries)
            if (e.Job is { IsFinished: false } j) _app.Queue.Cancel(j);
        SetStatus("Stopping...");
    }

    /// <summary>Polls the jobs (they run on worker threads) and shows their real state.</summary>
    private void RefreshJobs()
    {
        if (_archiveCts is not null)
        {
            RefreshArchive();
            return;
        }

        var jobs = _entries.Where(e => e.Job is not null).Select(e => e.Job!).ToList();
        _list.BeginUpdate();
        foreach (var e in _entries)
        {
            if (e.Job is not { } job) continue;
            SetCell(e.Item, 3, Ui.Status(job));
            SetCell(e.Item, 4, Ui.Result(job));
        }
        _list.EndUpdate();

        if (!_running) return;
        var percent = Ui.OverallPercent(jobs);
        ShowPercent(percent);

        if (jobs.All(j => j.IsFinished))
        {
            Finish(jobs);
            return;
        }

        var done = jobs.Count(j => j.IsFinished);
        var running = jobs.FirstOrDefault(j => j.Status == JobStatus.Running);
        NextFlavour(running?.Progress.Stage);
        var prefix = running is null ? "Waiting..." : $"{Ui.ActionWord(running)} {running.FileName}";
        var left = Ui.Remaining(_startedAt, percent);
        SetStatus($"{prefix}   ({done} of {jobs.Count} done{(left is null ? "" : ", " + left)})" + (_flavour.Length > 0 ? "   " + _flavour : ""));
    }

    private void RefreshArchive()
    {
        if (_archiveProgress is not { } p) { ShowPercent(null); SetStatus("Adding to ZIP..."); return; }
        var percent = (int)p.Percent;
        ShowPercent(percent);
        NextFlavour(ConversionStage.Converting);
        var left = Ui.Remaining(_startedAt, p.Percent);
        SetStatus($"Adding {p.CurrentEntry}   ({p.FilesDone} of {p.FileCount} files{(left is null ? "" : ", " + left)})" + (_flavour.Length > 0 ? "   " + _flavour : ""));

        var current = _entries.FirstOrDefault(e => p.CurrentEntry.Length > 0 && e.Path.Replace('\\', '/').EndsWith(p.CurrentEntry, StringComparison.OrdinalIgnoreCase));
        foreach (var e in _entries)
        {
            var status = e == current ? "Adding..." : e.Item.SubItems[3].Text == "Adding..." ? "Added" : e.Item.SubItems[3].Text;
            SetCell(e.Item, 3, status);
        }
    }

    private void ShowPercent(int? percent)
    {
        Ui.ShowProgress(_progress, percent);
        TaskbarProgress.Set(this, percent);
    }

    private void NextFlavour(ConversionStage? stage)
    {
        if (!_app.Settings.Read().EasterEggs || stage is null) { _flavour = ""; return; }
        if (DateTime.Now < _nextMessageAt) return;
        _flavour = _rotator.Next(DogeMessages.ForStage(stage.Value));
        _nextMessageAt = DateTime.Now + _rotator.NextInterval();
    }

    private static void SetCell(ListViewItem item, int index, string text)
    {
        if (item.SubItems[index].Text != text) item.SubItems[index].Text = text;
    }

    private void Finish(List<ConversionJob> jobs)
    {
        var ok = jobs.Where(j => j.Status == JobStatus.Succeeded).ToList();
        var failed = jobs.Count(j => j.Status == JobStatus.Failed);
        var cancelled = jobs.Count(j => j.Status == JobStatus.Cancelled);
        var skipped = jobs.Count(j => j.Status == JobStatus.Skipped) + _entries.Count(e => e.Job is null && e.Note is not null);

        var parts = new List<string>();
        if (ok.Count > 0) parts.Add($"{ok.Count} done");
        if (failed > 0) parts.Add($"{failed} failed");
        if (cancelled > 0) parts.Add($"{cancelled} cancelled");
        if (skipped > 0) parts.Add($"{skipped} skipped");

        var before = ok.Sum(j => Math.Max(0, j.SourceBytes));
        var after = ok.Sum(j => j.Result?.OutputBytes ?? 0);
        var sizes = ok.Count > 0 ? "   Total: " + CompressionPresets.SizeChange(before, after) : "";
        var wow = ok.Count > 0 && failed == 0 && _app.Settings.Read().EasterEggs ? "   wow." : "";
        EndRun(string.Join(", ", parts) + "." + sizes + wow, failed > 0);
    }

    private void EndRun(string summary, bool failed)
    {
        _tick.Stop();
        _running = false;
        _flavour = "";
        Ui.SetDancing(_doge, false);
        Ui.ShowProgress(_progress, 100);
        if (failed) TaskbarProgress.Error(this);
        else TaskbarProgress.Clear(this);
        SetStatus(summary);
        UpdateState();
        if (failed) System.Media.SystemSounds.Exclamation.Play();
    }

    private void UpdateState()
    {
        _start.Enabled = CanStart(out _);
        _stop.Enabled = _running;
        _format.Enabled = _level.Enabled = _output.Enabled = !_running;
        var total = _entries.Sum(e => e.Size);
        _statusCount.Text = _entries.Count == 0 ? "No files" : $"{_entries.Count} file{(_entries.Count == 1 ? "" : "s")}, {FileService.HumanSize(total)}";
        if (_entries.Count == 0 && !_running && string.IsNullOrEmpty(_statusText.Text))
            _statusText.Text = "Drag files here, or use File > Add files.";
    }

    private void SetStatus(string text) => _statusText.Text = text;

    private void ShowOptions()
    {
        using var f = new OptionsForm(_app);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        _suppressEvents = true;
        try { var st = _app.Settings.Read(); RefreshLevelChoices(st); RefreshOutputChoices(st); RefreshFormatChoices(); }
        finally { _suppressEvents = false; }
        UpdateHint();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_running) return;
        var answer = MessageBox.Show(this, "Files are still being processed. Stop them and close?", "KwikKonvert",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        StopJobs();
    }
}
