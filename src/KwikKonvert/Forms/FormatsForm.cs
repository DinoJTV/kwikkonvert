using KwikKonvert.Core.Services;

namespace KwikKonvert.Forms;

/// <summary>Help > Formats on this PC: what Windows' installed codecs can read and write here.</summary>
internal sealed class FormatsForm : Form
{
    public FormatsForm(AppHost app)
    {
        SuspendLayout();
        Ui.Prepare(this, "Formats on this PC");
        ClientSize = new Size(560, 440);
        MinimumSize = new Size(420, 300);
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        var list = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, ShowGroups = true };
        list.Columns.Add("Format", 80);
        list.Columns.Add("Description", 280);
        list.Columns.Add("Open", 60);
        list.Columns.Add("Save", 60);

        var groups = new Dictionary<FormatCategory, ListViewGroup>
        {
            [FormatCategory.Video] = new("Video"),
            [FormatCategory.Audio] = new("Audio"),
            [FormatCategory.Image] = new("Pictures"),
            [FormatCategory.Other] = new("Other files"),
        };
        foreach (var g in groups.Values) list.Groups.Add(g);

        foreach (var f in app.Formats.All.Where(f => !f.Id.Contains('.')).OrderBy(f => f.Category).ThenBy(f => f.Id))
        {
            list.Items.Add(new ListViewItem([f.Label, f.Description, f.CanRead ? "Yes" : "", f.CanWrite ? "Yes" : ""], groups[f.Category]));
        }

        var note = new Label
        {
            Text = "Any other file can be compressed into a ZIP. More formats appear when you install free codec extensions " +
                   "from the Microsoft Store (e.g. HEIF, WebP, AV1, VP9, Raw Image Extension).",
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 44,
            Padding = new Padding(0, 6, 0, 0),
        };
        var close = Ui.MakeButton("Close", (_, _) => Close());
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6) };
        bar.Controls.Add(close);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 8, 8, 0) };
        body.Controls.Add(list);
        body.Controls.Add(note);
        Controls.Add(body);
        Controls.Add(bar);
        CancelButton = close;
        ResumeLayout(true);
    }
}
