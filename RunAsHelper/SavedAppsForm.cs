using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using RunAsHelper.Core;
using RunAsHelper.Settings;

namespace RunAsHelper;

internal sealed class SavedAppsForm : Form
{
    private readonly AppSettings _settings;
    private readonly PipeClient  _client;

    private readonly ListView _listView    = new();
    private readonly Button   _btnLaunch   = new();
    private readonly Button   _btnRename   = new();
    private readonly Button   _btnRemove   = new();
    private readonly Button   _btnMoveUp   = new();
    private readonly Button   _btnMoveDown = new();

    public SavedAppsForm(AppSettings settings, PipeClient client)
    {
        _settings = settings;
        _client   = client;
        BuildLayout();
        RefreshList();
    }

    private void BuildLayout()
    {
        Text            = "Saved Applications";
        ClientSize      = new Size(600, 320);
        MinimumSize     = new Size(450, 260);
        StartPosition   = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;

        // ── ListView ────────────────────────────────────────────────────────
        _listView.View         = View.Details;
        _listView.FullRowSelect = true;
        _listView.GridLines     = true;
        _listView.MultiSelect   = false;
        _listView.LabelEdit     = true;
        _listView.Location      = new Point(8, 8);
        _listView.Size          = new Size(584, 262);
        _listView.Anchor        = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        _listView.Columns.Add("Name", 150);
        _listView.Columns.Add("Command Line", 295);
        _listView.Columns.Add("Priority", 85);

        _listView.SelectedIndexChanged += (_, _) => UpdateButtonStates();
        _listView.DoubleClick          += async (_, _) => await LaunchSelectedAsync();
        _listView.AfterLabelEdit       += ListView_AfterLabelEdit;
        _listView.KeyDown              += ListView_KeyDown;

        // ── Bottom buttons ───────────────────────────────────────────────────
        const int btnY = 284;
        const int btnH = 26;

        ConfigureButton(_btnLaunch,   "Launch",   new Point(8,   btnY), new Size(80, btnH), AnchorStyles.Bottom | AnchorStyles.Left);
        ConfigureButton(_btnRename,   "Rename",   new Point(92,  btnY), new Size(80, btnH), AnchorStyles.Bottom | AnchorStyles.Left);
        ConfigureButton(_btnRemove,   "Remove",   new Point(176, btnY), new Size(80, btnH), AnchorStyles.Bottom | AnchorStyles.Left);
        ConfigureButton(_btnMoveUp,   "↑",        new Point(268, btnY), new Size(36, btnH), AnchorStyles.Bottom | AnchorStyles.Left);
        ConfigureButton(_btnMoveDown, "↓",        new Point(308, btnY), new Size(36, btnH), AnchorStyles.Bottom | AnchorStyles.Left);

        var btnClose = new Button();
        ConfigureButton(btnClose, "Close", new Point(517, btnY), new Size(75, btnH), AnchorStyles.Bottom | AnchorStyles.Right);
        btnClose.Click += (_, _) => Close();

        _btnLaunch.Click   += async (_, _) => await LaunchSelectedAsync();
        _btnRename.Click   += (_, _) => RenameSelected();
        _btnRemove.Click   += (_, _) => RemoveSelected();
        _btnMoveUp.Click   += (_, _) => MoveSelected(-1);
        _btnMoveDown.Click += (_, _) => MoveSelected(+1);

        Controls.AddRange(new Control[]
        {
            _listView,
            _btnLaunch, _btnRename, _btnRemove,
            _btnMoveUp, _btnMoveDown, btnClose,
        });
    }

    private static void ConfigureButton(Button b, string text, Point loc, Size sz, AnchorStyles anchor)
    {
        b.Text     = text;
        b.Location = loc;
        b.Size     = sz;
        b.Anchor   = anchor;
    }

    // ── List management ──────────────────────────────────────────────────────

    private void RefreshList()
    {
        int selectedIdx = _listView.SelectedItems.Count > 0
            ? _listView.SelectedItems[0].Index : -1;

        _listView.Items.Clear();
        foreach (var app in _settings.SavedApplications)
        {
            var item = new ListViewItem(app.Name) { Tag = app };
            item.SubItems.Add(app.EffectiveCommandLine);
            item.SubItems.Add(NativeMethods.PriorityClassName(app.Priority));
            _listView.Items.Add(item);
        }

        if (selectedIdx >= 0 && selectedIdx < _listView.Items.Count)
        {
            _listView.Items[selectedIdx].Selected = true;
            _listView.Items[selectedIdx].EnsureVisible();
        }

        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        bool has  = _listView.SelectedItems.Count > 0;
        int  idx  = has ? _listView.SelectedItems[0].Index : -1;
        _btnLaunch.Enabled   = has;
        _btnRename.Enabled   = has;
        _btnRemove.Enabled   = has;
        _btnMoveUp.Enabled   = has && idx > 0;
        _btnMoveDown.Enabled = has && idx < _listView.Items.Count - 1;
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    private async Task LaunchSelectedAsync()
    {
        if (_listView.SelectedItems.Count == 0) return;
        var app = (SavedApplication)_listView.SelectedItems[0].Tag!;

        if (!NativeMethods.WaitNamedPipeW(@"\\.\pipe\RunAsHelper", 500))
        {
            MessageBox.Show("RunAS Helper service is not running.", "RunAS Helper",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        bool ok = await _client.LaunchElevatedAsync(
            app.EffectiveCommandLine, app.Priority, app.WorkingDirectory, app.ShowWindow);
        if (!ok)
            MessageBox.Show($"Failed to launch {app.Name}.", "RunAS Helper",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void RenameSelected()
    {
        if (_listView.SelectedItems.Count == 0) return;
        _listView.SelectedItems[0].BeginEdit();
    }

    private void RemoveSelected()
    {
        if (_listView.SelectedItems.Count == 0) return;
        var item = _listView.SelectedItems[0];
        var app  = (SavedApplication)item.Tag!;

        if (MessageBox.Show($"Remove \"{app.Name}\"?", "RunAS Helper",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        _settings.RemoveSavedApp(app.Name);
        _listView.Items.Remove(item);
        UpdateButtonStates();
    }

    private void MoveSelected(int delta)
    {
        if (_listView.SelectedItems.Count == 0) return;
        int idx    = _listView.SelectedItems[0].Index;
        int newIdx = idx + delta;
        if (newIdx < 0 || newIdx >= _listView.Items.Count) return;

        _settings.MoveSavedApp(idx, newIdx);
        RefreshList();
        _listView.Items[newIdx].Selected = true;
        _listView.Items[newIdx].EnsureVisible();
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void ListView_AfterLabelEdit(object? sender, LabelEditEventArgs e)
    {
        if (e.Label is null) return;
        string newName = e.Label.Trim();
        if (string.IsNullOrWhiteSpace(newName)) { e.CancelEdit = true; return; }

        var item = _listView.Items[e.Item];
        var app  = (SavedApplication)item.Tag!;
        int idx  = _settings.SavedApplications.IndexOf(app);
        if (idx < 0) { e.CancelEdit = true; return; }

        var renamed = app with { Name = newName };
        _settings.SavedApplications[idx] = renamed;
        _settings.Save();
        item.Tag = renamed;
    }

    private void ListView_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Delete: RemoveSelected(); break;
            case Keys.F2:     RenameSelected(); break;
        }
    }
}
