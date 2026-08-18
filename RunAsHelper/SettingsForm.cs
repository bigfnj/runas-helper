using System;
using System.Drawing;
using System.Windows.Forms;
using RunAsHelper.Settings;

namespace RunAsHelper;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings   _settings;
    private readonly CheckBox      _chkStartWithWindows  = new();
    private readonly CheckBox      _chkStartMinimized    = new();
    private readonly CheckBox      _chkMinimizeToTray    = new();
    private readonly CheckBox      _chkShowNotifications = new();
    private readonly CheckBox      _chkEnableLogging     = new();
    private readonly CheckBox      _chkAllowCli          = new();
    private readonly NumericUpDown _nudGateMinutes       = new();
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
        ClientSize      = new Size(360, 340);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;

        int y = 16;
        const int rowH = 28;

        _chkStartWithWindows.Text     = "Start with Windows (tray icon at login)";
        _chkStartWithWindows.Location = new Point(16, y);
        _chkStartWithWindows.AutoSize = true;
        y += rowH;

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
        y += rowH;

        _chkEnableLogging.Text     = "Enable activity logging";
        _chkEnableLogging.Location = new Point(16, y);
        _chkEnableLogging.AutoSize = true;
        y += rowH;

        _chkAllowCli.Text     = "Allow command line (resets to off each launch)";
        _chkAllowCli.Location = new Point(16, y);
        _chkAllowCli.AutoSize = true;
        y += rowH;

        // The service enforces this ceiling; the tray only mirrors it. 0 means the gate
        // stays open until the tray closes it or exits, which is the pre-1.7.1 behaviour.
        var lblGate = new Label
        {
            Text     = "…auto-close it after (minutes, 0 = never):",
            Location = new Point(34, y + 3),
            AutoSize = true,
        };
        _nudGateMinutes.Location = new Point(268, y);
        _nudGateMinutes.Size     = new Size(58, 23);
        _nudGateMinutes.Minimum  = 0;
        _nudGateMinutes.Maximum  = 1440;
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
            _chkStartWithWindows, _chkStartMinimized, _chkMinimizeToTray, _chkShowNotifications,
            _chkEnableLogging, _chkAllowCli,
            lblGate, _nudGateMinutes,
            lblMaxMru, _nudMaxMru,
            btnOk, btnCancel,
        });
    }

    private void LoadValues()
    {
        _chkStartWithWindows.Checked  = _settings.StartWithWindows;
        _chkStartMinimized.Checked    = _settings.StartMinimized;
        _chkMinimizeToTray.Checked    = _settings.MinimizeToTray;
        _chkShowNotifications.Checked = _settings.ShowTrayNotifications;
        _chkEnableLogging.Checked     = _settings.EnableLogging;
        _chkAllowCli.Checked          = _settings.AllowCommandLine;
        _nudGateMinutes.Value         = Math.Clamp(_settings.CliGateMinutes, 0, 1440);
        _nudMaxMru.Value              = Math.Clamp(_settings.MaxMruEntries, 1, 50);
    }

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        _settings.StartWithWindows      = _chkStartWithWindows.Checked;
        _settings.StartMinimized        = _chkStartMinimized.Checked;
        _settings.MinimizeToTray        = _chkMinimizeToTray.Checked;
        _settings.ShowTrayNotifications = _chkShowNotifications.Checked;
        _settings.EnableLogging         = _chkEnableLogging.Checked;
        _settings.AllowCommandLine      = _chkAllowCli.Checked;
        _settings.CliGateMinutes        = (int)_nudGateMinutes.Value;
        _settings.MaxMruEntries         = (int)_nudMaxMru.Value;
        _settings.Save();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Apply(this);   // match whatever palette the app is using
    }
}
