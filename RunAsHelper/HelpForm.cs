using System.Drawing;
using System.Windows.Forms;

namespace RunAsHelper;

/// <summary>Tools → How to Use: shows <see cref="HelpText.Cli"/> in a read-only pane.</summary>
internal sealed class HelpForm : Form
{
    public HelpForm()
    {
        Text            = "How to Use RunAS Helper";
        ClientSize      = new Size(620, 520);
        MinimumSize     = new Size(460, 360);
        StartPosition   = FormStartPosition.CenterParent;
        try { Icon = new Icon(SystemIcons.Information, 32, 32); } catch { /* non-fatal */ }

        var text = new TextBox
        {
            Dock       = DockStyle.Fill,
            Multiline  = true,
            ReadOnly   = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap   = false,
            BackColor  = SystemColors.Window,
            Font       = new Font("Consolas", 9f),
            Text       = HelpText.Cli.Replace("\n", "\r\n"),
        };
        text.Select(0, 0);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        var btnClose = new Button
        {
            Text         = "Close",
            DialogResult = DialogResult.OK,
            Size         = new Size(80, 26),
            Anchor       = AnchorStyles.Right | AnchorStyles.Bottom,
        };
        btnClose.Location = new Point(bottom.ClientSize.Width - 90, 8);
        bottom.Controls.Add(btnClose);

        Controls.Add(text);
        Controls.Add(bottom);
        AcceptButton = btnClose;
        CancelButton = btnClose;
    }
}
