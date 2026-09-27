using KwikKonvert.Core.Services;

namespace KwikKonvert.Forms;

/// <summary>Tiny standard dialog: "Fit under [ 50 ] MB".</summary>
internal sealed class SizePromptForm : Form
{
    private readonly NumericUpDown _mb = new() { Minimum = 1, Maximum = 100_000, Width = 90, Margin = new Padding(3, 3, 3, 3) };

    public int Megabytes => (int)_mb.Value;

    public SizePromptForm(int currentMB)
    {
        SuspendLayout();
        Ui.Prepare(this, "Custom size");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _mb.Value = Math.Clamp(currentMB, 1, 100_000);

        var intro = Ui.MakeLabel("Make each file fit under this size:");
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        row.Controls.AddRange([_mb, Ui.MakeLabel("MB")]);
        var note = Ui.MakeLabel($"1 MB = {CompressionPresets.Megabyte:N0} bytes, so the result also fits limits that count in MiB.");
        note.ForeColor = SystemColors.GrayText;

        var ok = Ui.MakeButton("OK", (_, _) => { DialogResult = DialogResult.OK; Close(); });
        var cancel = Ui.MakeButton("Cancel", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([cancel, ok]);

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(10) };
        grid.Controls.Add(intro);
        grid.Controls.Add(row);
        grid.Controls.Add(note);
        grid.Controls.Add(buttons);
        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;
        ResumeLayout(true);
        Shown += (_, _) => { _mb.Select(0, _mb.Text.Length); _mb.Focus(); };
    }
}
