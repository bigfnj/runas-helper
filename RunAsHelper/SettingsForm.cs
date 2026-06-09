using System;
using System.Drawing;
using System.Windows.Forms;
using RunAsHelper.Settings;

namespace RunAsHelper;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings   _settings;
    private readonly CheckBox      _chkStartMinimized    = new();
    private readonly CheckBox      _chkMinimizeToTray    = new();
    private readonly CheckBox      _chkShowNotifications = new();
    private readonly NumericUpDown _nudMaxMru            = new();

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        BuildLayout();
        LoadValues();
    }

    private void BuildLayout()
    {
        Text            = "Settings";
        ClientSize      = new Size(330, 210);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;

        int y = 16;
        const int rowH = 28;

        _chkStartMinimized.Text     = "Start minimized to tray";
        _chkStartMinimized.Location = new Point(16, y);
        _chkStartMinimized.AutoSize = true;
        y += rowH;

        _chkMinimizeToTray.Text     = "Minimize to tray instead of taskbar";
        _chkMinimizeToTray.Location = new Point(16, y);
        _chkMinimizeToTray.AutoSize = true;
        y += rowH;

        _chkShowNotifications.Text     = "Show tray balloon notifications";
        _chkShowNotifications.Location = new Point(16, y);
        _chkShowNotifications.AutoSize = true;
        y += rowH + 8;

        var lblMaxMru = new Label
        {
            Text     = "Max recent entries (1–50):",
            Location = new Point(16, y + 3),
            AutoSize = true,
        };

        _nudMaxMru.Location  = new Point(210, y);
        _nudMaxMru.Size      = new Size(58, 23);
        _nudMaxMru.Minimum   = 1;
        _nudMaxMru.Maximum   = 50;
        y += rowH + 16;

        var btnOk = new Button
        {
            Text         = "OK",
            DialogResult = DialogResult.OK,
            Location     = new Point(164, y),
            Size         = new Size(75, 26),
        };

        var btnCancel = new Button
        {
            Text         = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location     = new Point(243, y),
            Size         = new Size(75, 26),
        };

        AcceptButton = btnOk;
        CancelButton = btnCancel;
        btnOk.Click += BtnOk_Click;

        Controls.AddRange(new Control[]
        {
            _chkStartMinimized, _chkMinimizeToTray, _chkShowNotifications,
            lblMaxMru, _nudMaxMru,
            btnOk, btnCancel,
        });
    }

    private void LoadValues()
    {
        _chkStartMinimized.Checked    = _settings.StartMinimized;
        _chkMinimizeToTray.Checked    = _settings.MinimizeToTray;
        _chkShowNotifications.Checked = _settings.ShowTrayNotifications;
        _nudMaxMru.Value              = Math.Clamp(_settings.MaxMruEntries, 1, 50);
    }

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        _settings.StartMinimized        = _chkStartMinimized.Checked;
        _settings.MinimizeToTray        = _chkMinimizeToTray.Checked;
        _settings.ShowTrayNotifications = _chkShowNotifications.Checked;
        _settings.MaxMruEntries         = (int)_nudMaxMru.Value;
        _settings.Save();
    }
}
