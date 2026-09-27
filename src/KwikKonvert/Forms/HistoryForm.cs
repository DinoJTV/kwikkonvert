using KwikKonvert.Core.Models;
using KwikKonvert.Core.Services;

namespace KwikKonvert.Forms;

/// <summary>Tools > History: the last conversions, stored only on this PC.</summary>
internal sealed class HistoryForm : Form
{
    private readonly AppHost _app;
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false, ShowItemToolTips = true };

    public HistoryForm(AppHost app)
    {
        _app = app;
        SuspendLayout();
        Ui.Prepare(this, "History");
        ClientSize = new Size(700, 420);
        MinimumSize = new Size(480, 300);
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        _list.Columns.Add("Date", 130);
        _list.Columns.Add("File", 200);
        _list.Columns.Add("To", 90);
        _list.Columns.Add("Result", 250);
        _list.ItemActivate += (_, _) => Open(reveal: false);
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };

        var open = Ui.MakeButton("&Open", (_, _) => Open(reveal: false));
        var reveal = Ui.MakeButton("Show in &folder", (_, _) => Open(reveal: true));
        var remove = Ui.MakeButton("&Remove", (_, _) => RemoveSelected());
        var clear = Ui.MakeButton("C&lear all", (_, _) =>
        {
            if (MessageBox.Show(this, "Clear the whole history? Your files are not touched.", "History",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            _app.History.Clear();
            Fill();
        });
        var close = Ui.MakeButton("Close", (_, _) => Close());

        var left = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Left };
        left.Controls.AddRange([open, reveal, remove, clear]);
        var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Right };
        right.Controls.Add(close);
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6) };
        bar.Controls.Add(left);
        bar.Controls.Add(right);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 8, 8, 0) };
        body.Controls.Add(_list);
        Controls.Add(body);
        Controls.Add(bar);
        CancelButton = close;
        ResumeLayout(true);

        Fill();
    }

    private void Fill()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in _app.History.Entries)
        {
            var result = e.Success
                ? $"{Path.GetFileName(e.OutputPath)}  ({FileService.HumanSize(e.OutputBytes)})"
                : "Failed: " + Ui.FirstLine(e.Error ?? "");
            var item = new ListViewItem([e.When.ToString("g"), Path.GetFileName(e.SourcePath), e.TargetFormat.ToUpperInvariant(), result])
            {
                Tag = e,
                ToolTipText = (e.Success ? e.OutputPath : e.Error) ?? "",
            };
            if (!e.Success) item.ForeColor = SystemColors.GrayText;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }

    private HistoryEntry? Current => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as HistoryEntry : null;

    private void Open(bool reveal)
    {
        if (Current is not { } e) return;
        var path = e.Success && e.OutputPath is not null ? e.OutputPath : e.SourcePath;
        Ui.TryOpen(this, path, reveal);
    }

    private void RemoveSelected()
    {
        foreach (ListViewItem i in _list.SelectedItems)
            if (i.Tag is HistoryEntry e) _app.History.Remove(e.Id);
        Fill();
    }
}
