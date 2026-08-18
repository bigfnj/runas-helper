using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using RunAsHelper.Core;
using RunAsHelper.Settings;

namespace RunAsHelper;

/// <summary>
/// Add / edit a saved application: file location, name, parameters, working
/// directory, window state and priority. Returns the built <see cref="SavedApplication"/>
/// via <see cref="Result"/>; <see cref="RunAfterSave"/> is true when the user
/// chose "Save &amp; Run".
/// </summary>
internal sealed class ItemEditForm : Form
{
    // Priority combo order → process-creation priority class (matches MainForm).
    private static readonly uint[] PriorityClasses =
    {
        NativeMethods.IDLE_PRIORITY_CLASS,
        NativeMethods.BELOW_NORMAL_PRIORITY_CLASS,
        NativeMethods.NORMAL_PRIORITY_CLASS,
        NativeMethods.ABOVE_NORMAL_PRIORITY_CLASS,
        NativeMethods.HIGH_PRIORITY_CLASS,
        NativeMethods.REALTIME_PRIORITY_CLASS,
    };

    private readonly TextBox  _txtLocation = new();
    private readonly TextBox  _txtName     = new();
    private readonly TextBox  _txtParam    = new();
    private readonly TextBox  _txtWorkDir  = new();
    private readonly ComboBox _comboPriority = new();
    private readonly ComboBox _comboAccount  = new();

    private readonly RadioButton _rbNormal    = new() { Text = "Normal" };
    private readonly RadioButton _rbMinimized = new() { Text = "Minimized" };
    private readonly RadioButton _rbMaximized = new() { Text = "Maximized" };
    private readonly RadioButton _rbHidden    = new() { Text = "Hidden" };

    /// <summary>The application as configured by the user (valid only on DialogResult.OK).</summary>
    public SavedApplication Result { get; private set; } = new();

    /// <summary>True when the user chose "Save &amp; Run".</summary>
    public bool RunAfterSave { get; private set; }

    public ItemEditForm(SavedApplication? existing)
    {
        BuildLayout(existing is not null);
        LoadValues(existing);
    }

    private void BuildLayout(bool editing)
    {
        Text            = editing ? "Edit Item" : "Add Application";
        ClientSize      = new Size(460, 416);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;

        const int left = 16, fieldW = 380, fullW = 428;
        int y = 12;

        // File Location + browse
        AddLabel("File Location:", left, y); y += 20;
        _txtLocation.Location = new Point(left, y);
        _txtLocation.Size     = new Size(fieldW, 23);
        var btnBrowseFile = MakeBrowse(new Point(left + fieldW + 6, y - 1));
        btnBrowseFile.Click += (_, _) => BrowseFile();
        y += 36;

        // Name
        AddLabel("Name:", left, y); y += 20;
        _txtName.Location = new Point(left, y);
        _txtName.Size     = new Size(fullW, 23);
        y += 36;

        // Parameter
        AddLabel("Parameter:", left, y); y += 20;
        _txtParam.Location = new Point(left, y);
        _txtParam.Size     = new Size(fullW, 23);
        y += 36;

        // Working Directory + browse
        AddLabel("Working Directory:", left, y); y += 20;
        _txtWorkDir.Location = new Point(left, y);
        _txtWorkDir.Size     = new Size(fieldW, 23);
        var btnBrowseDir = MakeBrowse(new Point(left + fieldW + 6, y - 1));
        btnBrowseDir.Click += (_, _) => BrowseFolder();
        y += 40;

        // Windows State (left) + Priority (right)
        AddLabel("Windows State:", left, y);
        AddLabel("Priority:", 270, y);
        y += 22;
        _rbNormal.Location    = new Point(left, y);
        _rbMaximized.Location = new Point(left + 120, y);
        _comboPriority.Location      = new Point(270, y - 2);
        _comboPriority.Size          = new Size(158, 23);
        _comboPriority.DropDownStyle = ComboBoxStyle.DropDownList;
        _comboPriority.Items.AddRange(new object[]
        { "Idle", "Below Normal", "Normal", "Above Normal", "High", "Realtime" });
        y += 26;
        _rbMinimized.Location = new Point(left, y);
        _rbHidden.Location    = new Point(left + 120, y);
        foreach (var rb in new[] { _rbNormal, _rbMinimized, _rbMaximized, _rbHidden })
            rb.AutoSize = true;

        // Run as (account): TrustedInstaller (SYSTEM + TI group) or pure SYSTEM.
        y += 34;
        AddLabel("Run as:", left, y + 2);
        _comboAccount.Location      = new Point(72, y);
        _comboAccount.Size          = new Size(180, 23);
        _comboAccount.DropDownStyle  = ComboBoxStyle.DropDownList;
        _comboAccount.Items.AddRange(new object[] { "TrustedInstaller", "SYSTEM" });

        // Buttons
        int btnY = ClientSize.Height - 38;
        var btnCancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel,
            Location = new Point(ClientSize.Width - 91, btnY), Size = new Size(75, 26),
        };
        var btnSaveRun = new Button
        {
            Text = "Save && Run",
            Location = new Point(ClientSize.Width - 91 - 110, btnY), Size = new Size(104, 26),
        };
        var btnSave = new Button
        {
            Text = "Save",
            Location = new Point(ClientSize.Width - 91 - 110 - 81, btnY), Size = new Size(75, 26),
        };
        btnSave.Click    += (_, _) => Commit(run: false);
        btnSaveRun.Click += (_, _) => Commit(run: true);

        AcceptButton = btnSave;
        CancelButton = btnCancel;

        Controls.AddRange(new Control[]
        {
            _txtLocation, btnBrowseFile,
            _txtName, _txtParam,
            _txtWorkDir, btnBrowseDir,
            _rbNormal, _rbMinimized, _rbMaximized, _rbHidden,
            _comboPriority, _comboAccount,
            btnSave, btnSaveRun, btnCancel,
        });
    }

    private void LoadValues(SavedApplication? a)
    {
        _txtLocation.Text = a?.Location ?? string.Empty;
        _txtName.Text     = a?.Name ?? string.Empty;
        _txtParam.Text    = a?.Parameter ?? string.Empty;
        _txtWorkDir.Text  = a?.WorkingDirectory ?? string.Empty;

        uint priority = a?.Priority ?? NativeMethods.NORMAL_PRIORITY_CLASS;
        int idx = Array.IndexOf(PriorityClasses, priority);
        _comboPriority.SelectedIndex = idx >= 0 ? idx : 2;   // default Normal

        _comboAccount.SelectedIndex =
            string.Equals(a?.Account, "system", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        switch (a?.WindowsState ?? WindowsState.Normal)
        {
            case WindowsState.Minimized: _rbMinimized.Checked = true; break;
            case WindowsState.Maximized: _rbMaximized.Checked = true; break;
            case WindowsState.Hidden:    _rbHidden.Checked    = true; break;
            default:                     _rbNormal.Checked    = true; break;
        }
    }

    // ── Browse helpers ─────────────────────────────────────────────────────

    private void BrowseFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title  = "Select program or file",
            Filter = "Programs (*.exe;*.com;*.bat)|*.exe;*.com;*.bat|All Files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _txtLocation.Text = dlg.FileName;

        // Auto-fill name (from file) and working directory (file's folder) only
        // when the user hasn't already filled them.
        if (string.IsNullOrWhiteSpace(_txtName.Text))
            _txtName.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
        if (string.IsNullOrWhiteSpace(_txtWorkDir.Text))
            _txtWorkDir.Text = Path.GetDirectoryName(dlg.FileName) ?? string.Empty;
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select working directory" };
        if (!string.IsNullOrWhiteSpace(_txtWorkDir.Text) && Directory.Exists(_txtWorkDir.Text))
            dlg.SelectedPath = _txtWorkDir.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _txtWorkDir.Text = dlg.SelectedPath;
    }

    // ── Commit ─────────────────────────────────────────────────────────────

    private void Commit(bool run)
    {
        string location = _txtLocation.Text.Trim();
        if (string.IsNullOrWhiteSpace(location))
        {
            MessageBox.Show(this, "Enter a file location.", "RunAS Helper",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Resolve now so we don't save an entry that will fail to launch: accept a
        // full path or a name on the PATH (e.g. notepad.exe, lusrmgr.msc); error
        // otherwise. Store the resolved full path for a reliable launch.
        string? resolved = NativeMethods.ResolvePath(location);
        if (resolved is null)
        {
            MessageBox.Show(this,
                $"Couldn't find \"{location}\".\n\n" +
                "Enter a full path, or a program/file name that's on the system PATH " +
                "(for example: notepad.exe, lusrmgr.msc).",
                "RunAS Helper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string name = _txtName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = Path.GetFileNameWithoutExtension(resolved);
        if (string.IsNullOrWhiteSpace(name))
            name = resolved;

        Result = new SavedApplication
        {
            Name             = name,
            Location         = resolved,
            Parameter        = _txtParam.Text.Trim(),
            Priority         = PriorityClasses[Math.Max(0, _comboPriority.SelectedIndex)],
            WorkingDirectory = _txtWorkDir.Text.Trim(),
            WindowsState     = SelectedState(),
            Account          = _comboAccount.SelectedIndex == 1 ? "system" : "ti",
        };
        RunAfterSave  = run;
        DialogResult  = DialogResult.OK;
        Close();
    }

    private WindowsState SelectedState()
    {
        if (_rbMinimized.Checked) return WindowsState.Minimized;
        if (_rbMaximized.Checked) return WindowsState.Maximized;
        if (_rbHidden.Checked)    return WindowsState.Hidden;
        return WindowsState.Normal;
    }

    // ── Small UI helpers ───────────────────────────────────────────────────

    private void AddLabel(string text, int x, int y)
        => Controls.Add(new Label { Text = text, Location = new Point(x, y), AutoSize = true });

    private static Button MakeBrowse(Point loc)
        => new() { Text = "...", Location = loc, Size = new Size(34, 25) };

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Apply(this);   // match whatever palette the app is using
    }
}
