using System.Drawing;
using System.Windows.Forms;

namespace RunAsHelper;

internal sealed class NamePromptForm : Form
{
    private readonly TextBox _txtName = new();

    public string EnteredName => _txtName.Text;

    public NamePromptForm(string defaultName)
    {
        Text            = "Save Application";
        ClientSize      = new Size(340, 104);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;

        var lbl = new Label
        {
            Text     = "Display name:",
            Location = new Point(10, 14),
            AutoSize = true,
        };

        _txtName.Text     = defaultName;
        _txtName.Location = new Point(10, 34);
        _txtName.Size     = new Size(320, 23);

        var btnOk = new Button
        {
            Text         = "OK",
            DialogResult = DialogResult.OK,
            Location     = new Point(174, 68),
            Size         = new Size(75, 26),
        };

        var btnCancel = new Button
        {
            Text         = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location     = new Point(255, 68),
            Size         = new Size(75, 26),
        };

        AcceptButton = btnOk;
        CancelButton = btnCancel;

        Controls.AddRange(new Control[] { lbl, _txtName, btnOk, btnCancel });
    }

    protected override void OnShown(System.EventArgs e)
    {
        base.OnShown(e);
        _txtName.SelectAll();
        _txtName.Focus();
    }
}
