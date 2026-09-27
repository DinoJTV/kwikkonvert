using KwikKonvert.Core.Models;
using KwikKonvert.Core.Services;
using KwikKonvert.Platform;

namespace KwikKonvert.Forms;

/// <summary>
/// Small progress dialog for right-click actions from Explorer (like 7-Zip's): starts at once, shows real progress,
/// time left and the taskbar bar, and closes itself when everything worked (unless "Close when finished" is unticked).
/// Two modes: per-file jobs (convert / compress / fit under a size), or one ZIP archive of everything.
/// </summary>
internal sealed class ProgressForm : Form
{
    private sealed class Row
    {
        public required ListViewItem Item { get; init; }
        public ConversionJob? Job { get; init; }
        /// <summary>Not queued because of a real problem (can't make that format). "Already small enough" isn't one.</summary>
        public bool Problem { get; init; }
    }

    private readonly AppHost _app;
    private readonly List<Row> _rows = [];
    private readonly MessageRotator _rotator = new();
    private readonly DateTime _startedAt = DateTime.Now;

    private readonly DogeBox _doge = Ui.MakeDoge(80);
    private readonly Label _elapsed = new() { AutoSize = true };
    private readonly Label _remaining = new() { AutoSize = true };
    private readonly Label _files = new() { AutoSize = true };
    private readonly Label _sizes = new() { AutoSize = true };
    private readonly Label _current = new() { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Height = 20, Margin = new Padding(3, 6, 3, 0) };
    private readonly Label _flavour = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 3) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Height = 18, Margin = new Padding(3, 6, 3, 6) };
    private readonly ListView _list = new();
    private readonly CheckBox _closeWhenDone = new() { Text = "C&lose when finished", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    private readonly Button _openFolder;
    private readonly Button _cancel;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 250 };

    private bool _finished;
    private DateTime _nextMessageAt;

    // Archive mode
    private CancellationTokenSource? _archiveCts;
    private ArchiveProgress? _archiveProgress;
    private string? _archiveResult;
    private bool _isArchive;

    public string ActionKey { get; }

    /// <summary>More files for the same action are merged in until the batch is finished (never for archives).</summary>
    public bool AcceptsMoreFiles => !_finished && !_isArchive;

    public ProgressForm(AppHost app, string actionKey)
    {
        _app = app;
        ActionKey = actionKey;
        _openFolder = Ui.MakeButton("Open &folder", (_, _) => OpenFolder());
        _cancel = Ui.MakeButton("Cancel", (_, _) => CancelOrClose());

        SuspendLayout();
        Ui.Prepare(this, TitleFor(actionKey));
        ClientSize = new Size(520, 400);
        MinimumSize = new Size(420, 360);
        MaximizeBox = false;
        ShowInTaskbar = true;

        var info = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0) };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        info.Controls.Add(Ui.MakeLabel("Elapsed time:"), 0, 0);
        info.Controls.Add(_elapsed, 1, 0);
        info.Controls.Add(Ui.MakeLabel("Remaining:"), 0, 1);
        info.Controls.Add(_remaining, 1, 1);
        info.Controls.Add(Ui.MakeLabel("Files:"), 0, 2);
        info.Controls.Add(_files, 1, 2);
        info.Controls.Add(Ui.MakeLabel("Size:"), 0, 3);
        info.Controls.Add(_sizes, 1, 3);
        foreach (Control c in info.Controls) c.Margin = new Padding(3, 3, 3, 3);

        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.Dock = DockStyle.Fill;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.Columns.Add("Name", 170);
        _list.Columns.Add("Status", 130);
        _list.Columns.Add("Result", 190);
        _list.ShowItemToolTips = true;
        _list.ItemActivate += (_, _) => OpenSelected();
        Ui.EnableDoubleBuffer(_list);

        _closeWhenDone.Checked = app.Settings.Read().CloseProgressWhenDone;
        _closeWhenDone.CheckedChanged += (_, _) => _app.Settings.Update(s => s.CloseProgressWhenDone = _closeWhenDone.Checked);
        _openFolder.Enabled = false;

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        buttons.Controls.AddRange([_cancel, _openFolder]);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(8) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // doge + info
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // current file
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // flavour
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // progress
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));      // list
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // buttons

        root.Controls.Add(_doge, 0, 0);
        root.Controls.Add(info, 1, 0);
        root.Controls.Add(_current, 0, 1);
        root.SetColumnSpan(_current, 2);
        root.Controls.Add(_flavour, 0, 2);
        root.SetColumnSpan(_flavour, 2);
        root.Controls.Add(_progress, 0, 3);
        root.SetColumnSpan(_progress, 2);
        root.Controls.Add(_list, 0, 4);
        root.SetColumnSpan(_list, 2);
        root.Controls.Add(_closeWhenDone, 0, 5);
        root.Controls.Add(buttons, 1, 5);

        Controls.Add(root);
        CancelButton = _cancel;

        _tick.Tick += (_, _) => UpdateView();
        FormClosing += (_, _) => CancelRunning();
        FormClosed += (_, _) => _tick.Dispose();
        ResumeLayout(true);

        Ui.SetDancing(_doge, true);
        UpdateView();
    }

    private static string TitleFor(string actionKey)
    {
        var parts = actionKey.Split(':');
        return parts[0] switch
        {
            "convert" when parts.Length > 1 => $"Converting to {parts[1].ToUpperInvariant()} - KwikKonvert",
            "compress" => "Compressing - KwikKonvert",
            "fit" when parts.Length > 1 => $"Fitting under {parts[1]} MB - KwikKonvert",
            "zip" => "Adding to ZIP - KwikKonvert",
            _ => "Converting - KwikKonvert",
        };
    }

    // ------------------------------------------------------------------ per-file mode

    /// <summary>Queues the files and shows them in the list.</summary>
    public void Add(IReadOnlyList<WorkItem> items)
    {
        _list.BeginUpdate();
        foreach (var w in items)
        {
            if (_rows.Any(r => string.Equals(r.Item.ToolTipText, w.Path, StringComparison.OrdinalIgnoreCase))) continue;
            var item = new ListViewItem([Path.GetFileName(w.Path), "Waiting", ""]) { ToolTipText = w.Path };
            _list.Items.Add(item);

            var source = _app.Formats.DetectOrGeneric(w.Path);
            if (w.TargetBytes is { } limit && AppHost.FileSize(w.Path) is { } size && size <= limit)
            {
                item.SubItems[1].Text = "Already small enough";
                item.SubItems[2].Text = $"{FileService.HumanSize(size)} - left as it is";
                _rows.Add(new Row { Item = item });
                continue;
            }
            var possible = FormatService.AreEquivalent(source.Id, w.Target)
                || _app.Formats.TargetsFor(source).Any(t => FormatService.AreEquivalent(t.Id, w.Target));
            if (!possible)
            {
                item.SubItems[1].Text = "Skipped";
                item.SubItems[2].Text = $"Can't make {w.Target.ToUpperInvariant()} from {source.Label}";
                _rows.Add(new Row { Item = item, Problem = true });
                continue;
            }
            var job = _app.Queue.Enqueue(w.Path, source, w.Target, outputFolder: null, deleteSourceWhenDone: false,
                squeeze: w.Squeeze, targetBytes: w.TargetBytes);
            _rows.Add(new Row { Item = item, Job = job });
        }
        _list.EndUpdate();
        _tick.Start();
        UpdateView();
    }

    private List<ConversionJob> Jobs => _rows.Where(r => r.Job is not null).Select(r => r.Job!).ToList();

    private void UpdateView()
    {
        _elapsed.Text = Ui.Elapsed(DateTime.Now - _startedAt);
        if (_isArchive) { RefreshArchive(); return; }

        var jobs = Jobs;
        _list.BeginUpdate();
        foreach (var r in _rows)
        {
            if (r.Job is not { } job) continue;
            var status = Ui.Status(job);
            var result = Ui.Result(job);
            if (r.Item.SubItems[1].Text != status) r.Item.SubItems[1].Text = status;
            if (r.Item.SubItems[2].Text != result) r.Item.SubItems[2].Text = result;
        }
        _list.EndUpdate();

        var done = jobs.Count(j => j.IsFinished);
        _files.Text = $"{done} of {jobs.Count}";
        var ok = jobs.Where(j => j.Status == JobStatus.Succeeded).ToList();
        _sizes.Text = ok.Count == 0
            ? FileService.HumanSize(jobs.Sum(j => Math.Max(0, j.SourceBytes)))
            : CompressionPresets.SizeChange(ok.Sum(j => Math.Max(0, j.SourceBytes)), ok.Sum(j => j.Result?.OutputBytes ?? 0));
        _openFolder.Enabled = ok.Count > 0;

        if (_finished) return;

        var running = jobs.FirstOrDefault(j => j.Status == JobStatus.Running);
        _current.Text = running is null ? (jobs.Count == 0 ? "" : "Waiting...") : $"{Ui.ActionWord(running)}: {running.FileName}";
        var percent = Ui.OverallPercent(jobs);
        ShowPercent(percent);
        Flavour(running?.Progress.Stage);

        if (jobs.All(j => j.IsFinished) && _rows.Count > 0)
            FinishJobs(jobs);
    }

    private void FinishJobs(List<ConversionJob> jobs)
    {
        var failed = jobs.Count(j => j.Status == JobStatus.Failed) + _rows.Count(r => r.Problem);
        var ok = jobs.Count(j => j.Status == JobStatus.Succeeded);
        var cancelled = jobs.Count(j => j.Status == JobStatus.Cancelled);
        var skipped = jobs.Count(j => j.Status == JobStatus.Skipped) + _rows.Count(r => r.Job is null && !r.Problem);

        var text = failed == 0 && cancelled == 0
            ? $"Finished: {ok} file{(ok == 1 ? "" : "s")}."
            : $"Finished: {ok} done, {failed} with problems{(cancelled > 0 ? $", {cancelled} cancelled" : "")}.";
        if (skipped > 0) text += $" {skipped} left as they were.";
        Finish(text, success: failed == 0 && cancelled == 0 && skipped == 0, failed: failed > 0, anyDone: ok > 0);
    }

    // ------------------------------------------------------------------ archive mode

    /// <summary>Adds everything to one new .zip (like 7-Zip's "Add to archive").</summary>
    public async void StartArchive(IReadOnlyList<string> paths, SqueezeLevel level)
    {
        _isArchive = true;
        _archiveCts = new CancellationTokenSource();
        _current.Text = "Looking at the files...";
        _tick.Start();

        foreach (var p in paths)
        {
            var item = new ListViewItem([Path.GetFileName(p.TrimEnd('\\')), "Waiting", Directory.Exists(p) ? "folder" : ""]) { ToolTipText = p };
            _list.Items.Add(item);
            _rows.Add(new Row { Item = item });
        }

        try
        {
            var plan = await Task.Run(() => ZipArchiver.Plan(paths));
            if (plan.Entries.Count == 0) throw new IOException("There's nothing to add: the files or folders are gone or empty.");
            _files.Text = $"0 of {plan.FileCount}";
            _sizes.Text = FileService.HumanSize(plan.TotalBytes);

            var progress = new Progress<ArchiveProgress>(p => _archiveProgress = p); // raised on this (UI) thread
            _archiveResult = await _app.CreateArchiveAsync(plan, level, progress, _archiveCts.Token);

            var zipSize = AppHost.FileSize(_archiveResult) ?? 0;
            _sizes.Text = CompressionPresets.SizeChange(plan.TotalBytes, zipSize);
            foreach (var r in _rows)
            {
                r.Item.SubItems[1].Text = "Added";
                r.Item.SubItems[2].Text = "in " + Path.GetFileName(_archiveResult);
            }
            _files.Text = $"{plan.FileCount} of {plan.FileCount}";
            _openFolder.Enabled = true;
            Finish($"Created {Path.GetFileName(_archiveResult)}.", success: true, failed: false, anyDone: true);
        }
        catch (OperationCanceledException)
        {
            foreach (var r in _rows) r.Item.SubItems[1].Text = "Cancelled";
            Finish("Cancelled. No archive was made.", success: false, failed: false, anyDone: false);
        }
        catch (Exception ex)
        {
            foreach (var r in _rows) r.Item.SubItems[1].Text = "Failed";
            if (_rows.Count > 0) _rows[0].Item.SubItems[2].Text = Ui.FirstLine(DogeMessages.Explain(ex));
            Finish("Couldn't create the archive: " + Ui.FirstLine(DogeMessages.Explain(ex)), success: false, failed: true, anyDone: false);
        }
        finally
        {
            _archiveCts?.Dispose();
            _archiveCts = null;
        }
    }

    private void RefreshArchive()
    {
        if (_finished || _archiveProgress is not { } p) return;
        _files.Text = $"{p.FilesDone} of {p.FileCount}";
        _current.Text = p.CurrentEntry.Length > 0 ? "Adding: " + p.CurrentEntry : "Finishing...";
        ShowPercent((int)p.Percent);
        Flavour(ConversionStage.Converting);

        // Mark the selected items that are fully in.
        foreach (var r in _rows)
        {
            var name = Path.GetFileName(r.Item.ToolTipText.TrimEnd('\\'));
            var busy = p.CurrentEntry.StartsWith(name, StringComparison.OrdinalIgnoreCase);
            var text = busy ? "Adding..." : r.Item.SubItems[1].Text == "Adding..." ? "Added" : r.Item.SubItems[1].Text;
            if (r.Item.SubItems[1].Text != text) r.Item.SubItems[1].Text = text;
        }
    }

    // ------------------------------------------------------------------ shared

    private void ShowPercent(int? percent)
    {
        Ui.ShowProgress(_progress, percent);
        TaskbarProgress.Set(this, percent);
        _remaining.Text = Ui.Remaining(_startedAt, percent) is { } left ? left.Replace(" left", "") : "";
    }

    private void Flavour(ConversionStage? stage)
    {
        if (!_app.Settings.Read().EasterEggs || stage is null || DateTime.Now < _nextMessageAt) return;
        _flavour.Text = _rotator.Next(DogeMessages.ForStage(stage.Value));
        _nextMessageAt = DateTime.Now + _rotator.NextInterval();
    }

    private void Finish(string text, bool success, bool failed, bool anyDone)
    {
        if (_finished) return;
        _finished = true;
        _tick.Stop();
        Ui.SetDancing(_doge, false);
        Ui.ShowProgress(_progress, 100);
        _remaining.Text = "";
        _cancel.Text = "Close";
        _current.Text = text;

        var eggs = _app.Settings.Read().EasterEggs;
        _flavour.Text = !eggs ? "" : failed ? "much error." : anyDone ? DogeMessages.BatchComplete : "";

        if (failed) TaskbarProgress.Error(this);
        else TaskbarProgress.Clear(this);

        if (success && _closeWhenDone.Checked)
        {
            Close();
            return;
        }
        if (failed)
        {
            System.Media.SystemSounds.Exclamation.Play();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }
    }

    private void CancelOrClose()
    {
        if (_finished) { Close(); return; }
        CancelRunning();
        _current.Text = "Cancelling...";
    }

    private void CancelRunning()
    {
        try { _archiveCts?.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var r in _rows)
            if (r.Job is { IsFinished: false } job) _app.Queue.Cancel(job);
    }

    private void OpenFolder()
    {
        var first = _archiveResult ?? _rows.Select(r => r.Job?.Result?.OutputPath).FirstOrDefault(p => p is not null);
        if (first is not null) Ui.TryOpen(this, first, reveal: true);
    }

    private void OpenSelected()
    {
        if (_list.SelectedItems.Count == 0) return;
        var row = _rows.FirstOrDefault(r => r.Item == _list.SelectedItems[0]);
        var path = _archiveResult ?? row?.Job?.Result?.OutputPath;
        if (path is not null) Ui.TryOpen(this, path, reveal: _archiveResult is not null);
    }
}
