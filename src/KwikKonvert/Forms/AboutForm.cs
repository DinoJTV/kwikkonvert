using KwikKonvert.Core.Services;

namespace KwikKonvert.Forms;

/// <summary>Help > About. The doge dances here too.</summary>
internal sealed class AboutForm : Form
{
    private int _pats;

    public AboutForm()
    {
        SuspendLayout();
        Ui.Prepare(this, "About KwikKonvert");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var doge = Ui.MakeDoge(128);
        Ui.SetDancing(doge, true);

        var version = typeof(AboutForm).Assembly.GetName().Version;
        var title = new Label { Text = $"KwikKonvert {version?.ToString(3)}", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        title.Font = new Font(Font, FontStyle.Bold);
        var tagline = new Label { Text = DogeMessages.Tagline, AutoSize = true };
        var body = new Label
        {
            Text = "Converts and compresses pictures, audio and video with the codecs built into Windows. " +
                   "Other files are compressed into ZIP archives.\r\n\r\n" +
                   "Everything happens on this PC. Nothing is uploaded, and nothing is ever overwritten.",
            AutoSize = true,
            MaximumSize = new Size(300, 0),
            Margin = new Padding(3, 10, 3, 3),
        };
        doge.Cursor = Cursors.Hand;
        doge.Click += (_, _) => tagline.Text = ++_pats switch
        {
            1 => DogeMessages.PatDoge,
            2 => "much pat. very appreciate.",
            3 => DogeMessages.Speed,
            _ => DogeMessages.Tagline,
        };
        doge.Click += (_, _) => { if (_pats > 3) _pats = 0; };

        var ok = Ui.MakeButton("OK", (_, _) => Close());
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        text.Controls.AddRange([title, tagline, body]);

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(10) };
        grid.Controls.Add(doge, 0, 0);
        grid.Controls.Add(text, 1, 0);
        grid.Controls.Add(ok, 1, 1);
        ok.Anchor = AnchorStyles.Right;
        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = ok;
        ResumeLayout(true);
    }
}
