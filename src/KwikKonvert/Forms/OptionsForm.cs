using KwikKonvert.Core.Models;
using KwikKonvert.Core.Services;

namespace KwikKonvert.Forms;

/// <summary>Tools > Options: a standard tabbed dialog. Nothing is saved until OK or Apply.</summary>
internal sealed class OptionsForm : Form
{
    private readonly AppHost _app;

    // General
    private readonly RadioButton _outSame = new() { Text = "&Same folder as the original file", AutoSize = true };
    private readonly RadioButton _outDesktop = new() { Text = "&Desktop", AutoSize = true };
    private readonly RadioButton _outDownloads = new() { Text = "Do&wnloads", AutoSize = true };
    private readonly RadioButton _outCustom = new() { Text = "This &folder:", AutoSize = true };
    private readonly TextBox _customFolder = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly ComboBox _collision = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox _defaultLevel = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly CheckBox _closeProgress = new() { Text = "&Close the progress window when everything worked", AutoSize = true };
    private readonly NumericUpDown _customMB = new() { Minimum = 1, Maximum = 100_000, Width = 80 };
    private readonly CheckBox _eggs = new() { Text = "Show &doge messages (such status, very wow)", AutoSize = true };

    // Explorer
    private readonly CheckBox _explorerConvert = new() { Text = "Add \"&KwikKonvert\" (convert) to the right-click menu of pictures, audio and video", AutoSize = true };
    private readonly CheckBox _explorerCompress = new() { Text = "Add \"&Compress with KwikKonvert\" (levels, fit under a size, one ZIP) to all files and folders", AutoSize = true };
    private readonly CheckBox _quickFormats = new() { Text = "Show &quick formats:", AutoSize = true };
    private readonly NumericUpDown _quickCount = new() { Minimum = 1, Maximum = 8, Width = 50 };
    private readonly CheckBox _favouritesInMenu = new() { Text = "Show &favourite formats", AutoSize = true };
    private readonly CheckBox _moreFormats = new() { Text = "Show \"&More formats...\"", AutoSize = true };

    // Formats
    private readonly CheckBox _remember = new() { Text = "&Remember favourite formats", AutoSize = true };
    private readonly CheckBox _smart = new() { Text = "&Suggest the format I usually pick for each file type", AutoSize = true };
    private readonly CheckedListBox _favourites = new() { CheckOnClick = true, Dock = DockStyle.Fill, IntegralHeight = false };

    // Instant rules
    private readonly ListView _rules = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false };
    private readonly ComboBox _ruleFrom = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly ComboBox _ruleTo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };

    private bool _clearHabits;

    public OptionsForm(AppHost app)
    {
        _app = app;
        SuspendLayout();
        Ui.Prepare(this, "Options");
        ClientSize = new Size(560, 440);
        MinimumSize = new Size(480, 400);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(GeneralPage());
        tabs.TabPages.Add(ExplorerPage());
        tabs.TabPages.Add(FormatsPage());
        tabs.TabPages.Add(RulesPage());

        var ok = Ui.MakeButton("OK", (_, _) => { if (Save()) { DialogResult = DialogResult.OK; Close(); } });
        var cancel = Ui.MakeButton("Cancel", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        var apply = Ui.MakeButton("&Apply", (_, _) => Save());
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6), WrapContents = false };
        buttons.Controls.AddRange([apply, cancel, ok]);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 8, 8, 0) };
        body.Controls.Add(tabs);
        Controls.Add(body);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        ResumeLayout(true);

        LoadValues();
    }

    private static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(8), UseVisualStyleBackColor = true };
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        return page;
    }

    private static TableLayoutPanel Stack(int columns = 1)
    {
        var t = new TableLayoutPanel { ColumnCount = columns, AutoScroll = true };
        for (var i = 0; i < columns; i++)
            t.ColumnStyles.Add(new ColumnStyle(i == columns - 1 ? SizeType.Percent : SizeType.AutoSize, 100));
        return t;
    }

    private static void AddRow(TableLayoutPanel t, Control c, int indent = 0, int top = 3)
    {
        c.Margin = new Padding(3 + indent, top, 3, 3);
        t.Controls.Add(c);
        t.SetColumnSpan(c, t.ColumnCount);
    }

    private TabPage GeneralPage()
    {
        var t = Stack();
        var outGroup = new GroupBox { Text = "Save new files in", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8) };
        var og = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        og.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        og.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        og.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var rb in new[] { _outSame, _outDesktop, _outDownloads })
        {
            og.Controls.Add(rb);
            og.SetColumnSpan(rb, 3);
        }
        var browse = new Button { Text = "...", Width = 32, UseVisualStyleBackColor = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Save new files in", UseDescriptionForTitle = true, SelectedPath = _customFolder.Text };
            if (dlg.ShowDialog(this) == DialogResult.OK) { _customFolder.Text = dlg.SelectedPath; _outCustom.Checked = true; }
        };
        og.Controls.Add(_outCustom);
        og.Controls.Add(_customFolder);
        og.Controls.Add(browse);
        _customFolder.TextChanged += (_, _) => { if (_customFolder.Focused) _outCustom.Checked = true; };
        outGroup.Controls.Add(og);
        AddRow(t, outGroup);

        _collision.Items.Add(new Choice<CollisionBehavior>(CollisionBehavior.AddNumber, "Add a number:  photo (1).jpg"));
        _collision.Items.Add(new Choice<CollisionBehavior>(CollisionBehavior.AddTimestamp, "Add date and time:  photo 2026-09-27 10.30.00.jpg"));
        foreach (var level in Enum.GetValues<SqueezeLevel>().Where(l => l != SqueezeLevel.None))
            _defaultLevel.Items.Add(new Choice<SqueezeLevel>(level, CompressionPresets.Describe(level)));

        AddRow(t, Ui.MakeLabel("If a file with the same name exists (nothing is ever overwritten):"), top: 10);
        AddRow(t, _collision, indent: 16);
        AddRow(t, Ui.MakeLabel("Default compression level (Automatic and the Explorer menu):"), top: 10);
        AddRow(t, _defaultLevel, indent: 16);
        var custom = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        custom.Controls.AddRange([Ui.MakeLabel("Custom \"fit under\" size:"), _customMB, Ui.MakeLabel("MB")]);
        AddRow(t, custom, top: 8);
        AddRow(t, _closeProgress, top: 12);
        AddRow(t, _eggs);
        return Page("General", t);
    }

    private TabPage ExplorerPage()
    {
        var t = Stack();
        AddRow(t, _explorerConvert);
        var quick = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        _quickFormats.Margin = new Padding(0, 5, 3, 3);
        quick.Controls.AddRange([_quickFormats, _quickCount]);
        AddRow(t, quick, indent: 20);
        AddRow(t, _favouritesInMenu, indent: 20);
        AddRow(t, _moreFormats, indent: 20);
        AddRow(t, _explorerCompress, top: 12);

        var note = Ui.MakeLabel("On Windows 11 these entries are under \"Show more options\" (or Shift+F10). " +
                                "KwikKonvert only changes your own user's settings; no administrator rights are needed.", wrap: true);
        note.MaximumSize = new Size(480, 0);
        note.ForeColor = SystemColors.GrayText;
        AddRow(t, note, top: 16);

        _explorerConvert.CheckedChanged += (_, _) => UpdateExplorerEnabled();
        _quickFormats.CheckedChanged += (_, _) => UpdateExplorerEnabled();
        return Page("Explorer menu", t);
    }

    private void UpdateExplorerEnabled()
    {
        var on = _explorerConvert.Checked;
        _quickFormats.Enabled = _favouritesInMenu.Enabled = _moreFormats.Enabled = on;
        _quickCount.Enabled = on && _quickFormats.Checked;
    }

    private TabPage FormatsPage()
    {
        var t = new TableLayoutPanel { ColumnCount = 2, RowCount = 5 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        t.Controls.Add(_remember, 0, 0);
        t.SetColumnSpan(_remember, 2);
        t.Controls.Add(_smart, 0, 1);
        t.SetColumnSpan(_smart, 2);
        t.Controls.Add(Ui.MakeLabel("Favourite formats (listed first everywhere):"), 0, 2);
        t.Controls.Add(_favourites, 0, 3);
        t.SetColumnSpan(_favourites, 2);

        var clear = Ui.MakeButton("Forget my &habits", (_, _) =>
        {
            _clearHabits = true;
            MessageBox.Show(this, "Your usual formats will be forgotten when you click OK or Apply.", "Options", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        t.Controls.Add(clear, 1, 4);

        foreach (var f in _app.Formats.Writable.OrderBy(f => f.Category).ThenBy(f => f.Id))
            _favourites.Items.Add(new Choice<string>(f.Id, $"{f.Label} - {f.Description}"));
        _remember.CheckedChanged += (_, _) => _favourites.Enabled = _remember.Checked;
        return Page("Formats", t);
    }

    private TabPage RulesPage()
    {
        var t = new TableLayoutPanel { ColumnCount = 1, RowCount = 3 };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = Ui.MakeLabel("Instant Konvert: with a rule, the right-click menu converts that file type straight away, without asking.", wrap: true);
        intro.MaximumSize = new Size(480, 0);
        t.Controls.Add(intro, 0, 0);

        _rules.Columns.Add("From", 120);
        _rules.Columns.Add("To", 120);
        t.Controls.Add(_rules, 0, 1);

        var add = Ui.MakeButton("A&dd", (_, _) => AddRule());
        var remove = Ui.MakeButton("R&emove", (_, _) =>
        {
            foreach (ListViewItem i in _rules.SelectedItems) _rules.Items.Remove(i);
        });
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        var from = Ui.MakeLabel("From:");
        var to = Ui.MakeLabel("to:");
        row.Controls.AddRange([from, _ruleFrom, to, _ruleTo, add, remove]);
        t.Controls.Add(row, 0, 2);

        foreach (var f in _app.Formats.Readable.Where(f => !f.Id.Contains('.') && f.Category != FormatCategory.Other).OrderBy(f => f.Id))
            _ruleFrom.Items.Add(new Choice<FormatInfo>(f, f.Label));
        _ruleFrom.SelectedIndexChanged += (_, _) => FillRuleTargets();
        if (_ruleFrom.Items.Count > 0) _ruleFrom.SelectedIndex = 0;
        return Page("Instant rules", t);
    }

    private void FillRuleTargets()
    {
        _ruleTo.Items.Clear();
        if (_ruleFrom.SelectedItem is not Choice<FormatInfo> from) return;
        foreach (var c in _app.Prefs.OrderedTargets(from.Value))
            _ruleTo.Items.Add(new Choice<string>(c.Format.Id, c.Format.Label));
        if (_ruleTo.Items.Count > 0) _ruleTo.SelectedIndex = 0;
    }

    private void AddRule()
    {
        if (_ruleFrom.SelectedItem is not Choice<FormatInfo> from || _ruleTo.SelectedItem is not Choice<string> to) return;
        foreach (ListViewItem existing in _rules.Items)
        {
            if (string.Equals(((InstantRule)existing.Tag!).From, from.Value.Id, StringComparison.OrdinalIgnoreCase))
            {
                _rules.Items.Remove(existing);
                break;
            }
        }
        AddRuleRow(new InstantRule { From = from.Value.Id, To = to.Value });
    }

    private void AddRuleRow(InstantRule rule) =>
        _rules.Items.Add(new ListViewItem([rule.From.ToUpperInvariant(), rule.To.ToUpperInvariant()]) { Tag = rule });

    // ------------------------------------------------------------------ load / save

    private void LoadValues()
    {
        var s = _app.Settings.Read();
        _outSame.Checked = s.OutputLocation == OutputLocation.SameFolder;
        _outDesktop.Checked = s.OutputLocation == OutputLocation.Desktop;
        _outDownloads.Checked = s.OutputLocation == OutputLocation.Downloads;
        _outCustom.Checked = s.OutputLocation == OutputLocation.Custom;
        _customFolder.Text = s.CustomOutputFolder ?? "";
        _collision.SelectedIndex = s.Collision == CollisionBehavior.AddTimestamp ? 1 : 0;
        var levelIndex = _defaultLevel.Items.Cast<Choice<SqueezeLevel>>().ToList().FindIndex(c => c.Value == s.DefaultSqueeze);
        _defaultLevel.SelectedIndex = levelIndex >= 0 ? levelIndex : 1;
        _closeProgress.Checked = s.CloseProgressWhenDone;
        _eggs.Checked = s.EasterEggs;
        _customMB.Value = Math.Clamp(s.CustomTargetMB, 1, 100_000);

        _explorerConvert.Checked = s.ExplorerEnabled;
        _explorerCompress.Checked = s.ExplorerCompress;
        _quickFormats.Checked = s.ExplorerQuickFormats;
        _quickCount.Value = Math.Clamp(s.ExplorerQuickCount, 1, 8);
        _favouritesInMenu.Checked = s.ExplorerFavourites;
        _moreFormats.Checked = s.ExplorerMoreFormats;
        UpdateExplorerEnabled();

        _remember.Checked = s.RememberFavourites;
        _smart.Checked = s.SmartSuggestions;
        _favourites.Enabled = s.RememberFavourites;
        for (var i = 0; i < _favourites.Items.Count; i++)
            _favourites.SetItemChecked(i, _app.Prefs.IsFavourite(((Choice<string>)_favourites.Items[i]).Value));

        _rules.Items.Clear();
        foreach (var r in s.InstantRules) AddRuleRow(new InstantRule { From = r.From, To = r.To });
    }

    private bool Save()
    {
        if (_outCustom.Checked && !Directory.Exists(_customFolder.Text))
        {
            MessageBox.Show(this, "The folder doesn't exist:\n" + _customFolder.Text, "Options", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var rules = _rules.Items.Cast<ListViewItem>().Select(i => (InstantRule)i.Tag!).ToList();
        _app.Settings.Update(s =>
        {
            s.OutputLocation = _outDesktop.Checked ? OutputLocation.Desktop
                : _outDownloads.Checked ? OutputLocation.Downloads
                : _outCustom.Checked ? OutputLocation.Custom
                : OutputLocation.SameFolder;
            if (!string.IsNullOrWhiteSpace(_customFolder.Text)) s.CustomOutputFolder = _customFolder.Text;
            s.Collision = (_collision.SelectedItem as Choice<CollisionBehavior>)?.Value ?? CollisionBehavior.AddNumber;
            s.DefaultSqueeze = (_defaultLevel.SelectedItem as Choice<SqueezeLevel>)?.Value ?? SqueezeLevel.Normal;
            s.CloseProgressWhenDone = _closeProgress.Checked;
            s.EasterEggs = _eggs.Checked;
            s.CustomTargetMB = (int)_customMB.Value;

            s.ExplorerEnabled = _explorerConvert.Checked;
            s.ExplorerCompress = _explorerCompress.Checked;
            s.ExplorerQuickFormats = _quickFormats.Checked;
            s.ExplorerQuickCount = (int)_quickCount.Value;
            s.ExplorerFavourites = _favouritesInMenu.Checked;
            s.ExplorerMoreFormats = _moreFormats.Checked;

            s.RememberFavourites = _remember.Checked;
            s.SmartSuggestions = _smart.Checked;
            s.InstantRules = rules;
        });

        if (_clearHabits)
        {
            // Clearing the store also clears favourites, so they're re-applied below from the checkboxes.
            _app.Prefs.Clear();
            _clearHabits = false;
        }
        if (_remember.Checked)
        {
            for (var i = 0; i < _favourites.Items.Count; i++)
            {
                var id = ((Choice<string>)_favourites.Items[i]).Value;
                if (_favourites.GetItemChecked(i) != _app.Prefs.IsFavourite(id)) _app.Prefs.ToggleFavourite(id);
            }
        }
        return true;
    }
}
