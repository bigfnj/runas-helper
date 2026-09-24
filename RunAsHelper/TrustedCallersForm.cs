using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RunAsHelper.Core;

namespace RunAsHelper;

/// <summary>
/// Manages the machine-wide user SID allowlist exposed by the service. The service,
/// not this form, enforces that only the installed elevated tray may make changes.
/// </summary>
internal sealed class TrustedCallersForm : Form
{
    private readonly PipeClient _client;
    private readonly ListView _callers = new();
    private readonly Label _warning = new();
    private readonly Label _status = new();
    private readonly Button _addLocal = new();
    private readonly Button _findAnother = new();
    private readonly Button _remove = new();
    private readonly Button _refresh = new();
    private readonly HashSet<string> _trustedSids = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();

    private bool _busy;
    private bool _managementAvailable;
    private bool _closing;

    public TrustedCallersForm(PipeClient client)
    {
        _client = client;
        BuildLayout();
        WireEvents();
    }

    private void BuildLayout()
    {
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Text = "Trusted command-line users";
        ClientSize = new Size(820, 500);
        MinimumSize = new Size(680, 420);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 4,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        _warning.Dock = DockStyle.Fill;
        _warning.Padding = new Padding(10, 6, 10, 6);
        _warning.BorderStyle = BorderStyle.FixedSingle;
        _warning.TextAlign = ContentAlignment.MiddleLeft;
        _warning.Font = new Font(_warning.Font, FontStyle.Bold);
        _warning.Text =
            "Security warning: any process running as a user listed below can launch arbitrary " +
            "commands as SYSTEM or TrustedInstaller through RunAS Helper, even while the general " +
            "command-line gate is closed. Add only individual accounts you fully trust.";

        _callers.Dock = DockStyle.Fill;
        _callers.View = View.Details;
        _callers.FullRowSelect = true;
        _callers.MultiSelect = false;
        _callers.HideSelection = false;
        _callers.GridLines = true;
        _callers.ShowItemToolTips = true;
        _callers.AccessibleName = "Trusted command-line users";
        _callers.Columns.Add("Account", 265);
        _callers.Columns.Add("Status", 125);
        _callers.Columns.Add("SID", 390);

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.AutoEllipsis = true;
        _status.Text = "Loading trusted users...";

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 7, 0, 0),
        };

        var close = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.Cancel,
            Size = new Size(82, 27),
        };
        _refresh.Text = "Refresh";
        _refresh.Size = new Size(82, 27);
        _remove.Text = "Remove";
        _remove.Size = new Size(82, 27);
        _findAnother.Text = "Find another user...";
        _findAnother.Size = new Size(142, 27);
        _addLocal.Text = "Add local user...";
        _addLocal.Size = new Size(128, 27);

        buttons.Controls.AddRange(new Control[]
        {
            close, _refresh, _remove, _findAnother, _addLocal,
        });

        root.Controls.Add(_warning, 0, 0);
        root.Controls.Add(_callers, 0, 1);
        root.Controls.Add(_status, 0, 2);
        root.Controls.Add(buttons, 0, 3);
        Controls.Add(root);
        CancelButton = close;
    }

    private void WireEvents()
    {
        _callers.SelectedIndexChanged += (_, _) => UpdateButtonStates();
        _callers.Resize += (_, _) => StretchSidColumn();
        _addLocal.Click += async (_, _) => await AddLocalUserAsync();
        _findAnother.Click += async (_, _) => await FindAnotherUserAsync();
        _remove.Click += async (_, _) => await RemoveSelectedAsync();
        _refresh.Click += async (_, _) => await RefreshTrustedCallersAsync();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        StretchSidColumn();

        if (!NativeMethods.IsUserAnAdmin())
        {
            _managementAvailable = false;
            _status.Text =
                "Activate RunAS Helper first. Only the installed, elevated tray can manage this list.";
            _status.ForeColor = Theme.Danger;
            UpdateButtonStates();
            return;
        }

        await RefreshTrustedCallersAsync();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Apply(this);
        _warning.ForeColor = Theme.Warn;
    }

    private void StretchSidColumn()
    {
        if (_callers.Columns.Count != 3) return;
        int remaining = _callers.ClientSize.Width
                        - _callers.Columns[0].Width
                        - _callers.Columns[1].Width
                        - SystemInformation.VerticalScrollBarWidth
                        - 4;
        _callers.Columns[2].Width = Math.Max(220, remaining);
    }

    private async Task RefreshTrustedCallersAsync()
    {
        string? selectedSid = SelectedSid();
        SetBusy(true, "Reading trusted users from the service...");
        try
        {
            var (ok, sids) = await _client.ListTrustedCallersAsync(_lifetime.Token);
            if (_closing || IsDisposed || Disposing) return;
            if (!ok)
            {
                _managementAvailable = false;
                _status.Text =
                    "The service rejected policy access. Use the installed RunAsHelper.exe, activate it, and verify the service is running.";
                _status.ForeColor = Theme.Danger;
                return;
            }

            _managementAvailable = true;
            _trustedSids.Clear();
            foreach (string sid in sids) _trustedSids.Add(sid);

            IReadOnlyList<LocalWindowsAccount> localUsers;
            try
            {
                localUsers = await Task.Run(WindowsAccountResolver.EnumerateLocalUsers, _lifetime.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                localUsers = Array.Empty<LocalWindowsAccount>();
            }

            var localBySid = localUsers
                .Where(user => user.Sid.Length != 0)
                .GroupBy(user => user.Sid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var rows = await Task.Run(
                () => BuildRows(_trustedSids, localBySid),
                _lifetime.Token);
            if (_closing || IsDisposed || Disposing) return;

            _callers.BeginUpdate();
            try
            {
                _callers.Items.Clear();
                foreach (var row in rows)
                {
                    var item = new ListViewItem(row.AccountName)
                    {
                        Tag = row.Sid,
                        ToolTipText = row.ToolTip,
                    };
                    item.SubItems.Add(row.Status);
                    item.SubItems.Add(row.Sid);
                    _callers.Items.Add(item);
                    if (string.Equals(row.Sid, selectedSid, StringComparison.OrdinalIgnoreCase))
                        item.Selected = true;
                }
            }
            finally
            {
                _callers.EndUpdate();
            }

            _status.ForeColor = Theme.Muted;
            _status.Text = rows.Count == 0
                ? "No trusted users. Command-line callers still need the general CLI gate to be open."
                : $"{rows.Count} trusted user{(rows.Count == 1 ? string.Empty : "s")}. Policy is stored machine-wide.";
        }
        catch (OperationCanceledException)
        {
            // Closing the dialog cancels any outstanding pipe or account lookup.
        }
        finally
        {
            if (!_closing && !IsDisposed && !Disposing) SetBusy(false);
        }
    }

    private static List<TrustedCallerRow> BuildRows(
        IEnumerable<string> sids,
        IReadOnlyDictionary<string, LocalWindowsAccount> localBySid)
    {
        var rows = new List<TrustedCallerRow>();
        foreach (string sid in sids.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            if (localBySid.TryGetValue(sid, out var local))
            {
                rows.Add(new TrustedCallerRow(
                    local.AccountName,
                    $"{local.Status} (local)",
                    sid,
                    local.Description));
                continue;
            }

            if (WindowsAccountResolver.TryResolveSid(sid, out var account, out string error))
            {
                rows.Add(new TrustedCallerRow(account!.AccountName, "Resolved", sid, account.AccountName));
            }
            else
            {
                rows.Add(new TrustedCallerRow("(unresolved account)", "Unresolved", sid, error));
            }
        }

        rows.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.AccountName, right.AccountName));
        return rows;
    }

    private async Task AddLocalUserAsync()
    {
        SetBusy(true, "Enumerating local Windows users...");
        try
        {
            var users = await Task.Run(WindowsAccountResolver.EnumerateLocalUsers, _lifetime.Token);
            if (_closing || IsDisposed || Disposing) return;
            SetBusy(false);

            using var picker = new LocalUserPickerForm(users, _trustedSids);
            if (picker.ShowDialog(this) == DialogResult.OK && picker.SelectedAccount is not null)
                await AddAccountAsync(picker.SelectedAccount);
        }
        catch (OperationCanceledException)
        {
            // Dialog is closing.
        }
        catch (Exception ex)
        {
            if (_closing || IsDisposed || Disposing) return;
            MessageBox.Show(this,
                $"Windows could not enumerate local users.\n\n{ex.Message}",
                "Local user lookup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!_closing && !IsDisposed && !Disposing) SetBusy(false);
        }
    }

    private async Task FindAnotherUserAsync()
    {
        using var lookup = new AccountLookupForm();
        if (lookup.ShowDialog(this) == DialogResult.OK && lookup.SelectedAccount is not null)
            await AddAccountAsync(lookup.SelectedAccount);
    }

    private async Task AddAccountAsync(ResolvedWindowsAccount account)
    {
        if (_trustedSids.Contains(account.Sid))
        {
            MessageBox.Show(this,
                $"{account.AccountName} is already trusted.",
                "Already trusted", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            $"Grant complete command-line launch access to {account.AccountName}?\n\n" +
            "Any process running as this user will be able to launch arbitrary commands as SYSTEM or " +
            "TrustedInstaller through RunAS Helper, even while the general command-line gate is closed. " +
            "This is persistent, machine-wide access.\n\n" +
            "Continue?",
            "Grant privileged command-line access?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        SetBusy(true, $"Adding {account.AccountName}...");
        try
        {
            bool ok = await _client.AddTrustedCallerAsync(account.Sid, _lifetime.Token);
            if (_closing || IsDisposed || Disposing) return;
            if (!ok)
            {
                ShowPolicyRejected("add that user");
                return;
            }

            await RefreshTrustedCallersAsync();
        }
        catch (OperationCanceledException)
        {
            // Dialog is closing.
        }
        finally
        {
            if (!_closing && !IsDisposed && !Disposing) SetBusy(false);
        }
    }

    private async Task RemoveSelectedAsync()
    {
        string? sid = SelectedSid();
        if (sid is null) return;

        string accountName = _callers.SelectedItems[0].Text;
        var answer = MessageBox.Show(this,
            $"Remove {accountName} from trusted command-line users?\n\n" +
            "The account will again require the general CLI gate to be open. " +
            "This does not stop commands that are already running.",
            "Remove trusted user?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        SetBusy(true, $"Removing {accountName}...");
        try
        {
            bool ok = await _client.RemoveTrustedCallerAsync(sid, _lifetime.Token);
            if (_closing || IsDisposed || Disposing) return;
            if (!ok)
            {
                ShowPolicyRejected("remove that user");
                return;
            }

            await RefreshTrustedCallersAsync();
        }
        catch (OperationCanceledException)
        {
            // Dialog is closing.
        }
        finally
        {
            if (!_closing && !IsDisposed && !Disposing) SetBusy(false);
        }
    }

    private void ShowPolicyRejected(string action)
    {
        MessageBox.Show(this,
            $"RunAs Helper could not {action}.\n\n" +
            "Policy changes are accepted only from the installed RunAsHelper.exe running elevated. " +
            "Activate the tray and verify the service is running, then try again.",
            "Policy change rejected", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private string? SelectedSid() =>
        _callers.SelectedItems.Count == 1 ? _callers.SelectedItems[0].Tag as string : null;

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        UseWaitCursor = busy;
        if (status is not null)
        {
            _status.Text = status;
            _status.ForeColor = Theme.Muted;
        }
        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        _addLocal.Enabled = _managementAvailable && !_busy;
        _findAnother.Enabled = _managementAvailable && !_busy;
        _remove.Enabled = _managementAvailable && !_busy && SelectedSid() is not null;
        _refresh.Enabled = !_busy && NativeMethods.IsUserAnAdmin();
    }

    private sealed record TrustedCallerRow(
        string AccountName,
        string Status,
        string Sid,
        string ToolTip);
}

/// <summary>Picker populated from NetUserEnum, so no SID knowledge is required.</summary>
internal sealed class LocalUserPickerForm : Form
{
    private readonly ListView _users = new();
    private readonly Button _add = new();
    private readonly IReadOnlySet<string> _alreadyTrusted;

    public ResolvedWindowsAccount? SelectedAccount { get; private set; }

    public LocalUserPickerForm(
        IReadOnlyList<LocalWindowsAccount> users,
        IReadOnlySet<string> alreadyTrusted)
    {
        _alreadyTrusted = alreadyTrusted;
        BuildLayout(users);
    }

    private void BuildLayout(IReadOnlyList<LocalWindowsAccount> users)
    {
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Text = "Add a local user";
        ClientSize = new Size(680, 410);
        MinimumSize = new Size(560, 340);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var intro = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Choose an individual local Windows user. Groups are not listed and cannot be added.",
        };

        _users.Dock = DockStyle.Fill;
        _users.View = View.Details;
        _users.FullRowSelect = true;
        _users.MultiSelect = false;
        _users.HideSelection = false;
        _users.GridLines = true;
        _users.ShowItemToolTips = true;
        _users.AccessibleName = "Local Windows users";
        _users.Columns.Add("Account", 260);
        _users.Columns.Add("Status", 120);
        _users.Columns.Add("Description", 255);

        foreach (var user in users)
        {
            bool trusted = user.Sid.Length != 0 && _alreadyTrusted.Contains(user.Sid);
            string status = trusted ? "Already trusted" : user.Status;
            var item = new ListViewItem(user.AccountName)
            {
                Tag = user,
                ToolTipText = user.ResolutionError ?? user.Description,
            };
            item.SubItems.Add(status);
            item.SubItems.Add(user.ResolutionError ?? user.Description);
            _users.Items.Add(item);
        }

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 7, 0, 0),
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Size = new Size(82, 27),
        };
        _add.Text = "Add user";
        _add.Size = new Size(90, 27);
        _add.Enabled = false;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_add);

        root.Controls.Add(intro, 0, 0);
        root.Controls.Add(_users, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
        AcceptButton = _add;
        CancelButton = cancel;

        _users.SelectedIndexChanged += (_, _) => UpdateAddButton();
        _users.DoubleClick += (_, _) => AcceptSelection();
        _users.Resize += (_, _) => StretchDescriptionColumn();
        _add.Click += (_, _) => AcceptSelection();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        StretchDescriptionColumn();
        if (_users.Items.Count > 0)
        {
            _users.Items[0].Selected = true;
            _users.Select();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Apply(this);
    }

    private void StretchDescriptionColumn()
    {
        if (_users.Columns.Count != 3) return;
        int remaining = _users.ClientSize.Width
                        - _users.Columns[0].Width
                        - _users.Columns[1].Width
                        - SystemInformation.VerticalScrollBarWidth
                        - 4;
        _users.Columns[2].Width = Math.Max(140, remaining);
    }

    private void UpdateAddButton()
    {
        if (_users.SelectedItems.Count != 1 ||
            _users.SelectedItems[0].Tag is not LocalWindowsAccount account)
        {
            _add.Enabled = false;
            return;
        }

        _add.Enabled = account.CanSelect && !_alreadyTrusted.Contains(account.Sid);
    }

    private void AcceptSelection()
    {
        if (!_add.Enabled || _users.SelectedItems.Count != 1 ||
            _users.SelectedItems[0].Tag is not LocalWindowsAccount account)
            return;

        SelectedAccount = new ResolvedWindowsAccount(account.AccountName, account.Sid);
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>Account-name lookup for domain, Entra-backed, or other non-local users.</summary>
internal sealed class AccountLookupForm : Form
{
    private readonly TextBox _accountName = new();
    private readonly Label _feedback = new();
    private readonly Button _find = new();
    private readonly CancellationTokenSource _lifetime = new();

    public ResolvedWindowsAccount? SelectedAccount { get; private set; }

    public AccountLookupForm()
    {
        BuildLayout();
    }

    private void BuildLayout()
    {
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Text = "Find another Windows user";
        ClientSize = new Size(520, 190);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var prompt = new Label
        {
            Location = new Point(16, 14),
            Size = new Size(488, 38),
            Text = "Enter an account as DOMAIN\\user, user@domain, or a local user name. " +
                   "RunAs Helper will look up and store its SID for you.",
        };
        var nameLabel = new Label
        {
            Location = new Point(16, 61),
            Size = new Size(100, 23),
            Text = "Account name:",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _accountName.Location = new Point(118, 61);
        _accountName.Size = new Size(386, 23);
        _accountName.AccessibleName = "Windows account name";

        _feedback.Location = new Point(16, 92);
        _feedback.Size = new Size(488, 36);
        _feedback.ForeColor = Theme.Danger;

        _find.Text = "Find user";
        _find.Location = new Point(323, 145);
        _find.Size = new Size(88, 27);
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(417, 145),
            Size = new Size(87, 27),
        };

        Controls.AddRange(new Control[]
        {
            prompt, nameLabel, _accountName, _feedback, _find, cancel,
        });
        AcceptButton = _find;
        CancelButton = cancel;
        _find.Click += async (_, _) => await ResolveAsync();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _accountName.Select();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Apply(this);
        _feedback.ForeColor = Theme.Danger;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnFormClosed(e);
    }

    private async Task ResolveAsync()
    {
        string input = _accountName.Text.Trim();
        if (input.Length == 0)
        {
            _feedback.Text = "Enter a Windows account name.";
            return;
        }

        _accountName.Enabled = false;
        _find.Enabled = false;
        _feedback.ForeColor = Theme.Muted;
        _feedback.Text = "Looking up the account...";
        UseWaitCursor = true;
        CancellationToken token = _lifetime.Token;
        try
        {
            var result = await Task.Run(() =>
            {
                bool ok = WindowsAccountResolver.TryResolveUser(input, out var account, out string error);
                return (ok, account, error);
            }, token);

            if (token.IsCancellationRequested || IsDisposed || Disposing) return;

            if (result.ok)
            {
                SelectedAccount = result.account;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            _feedback.ForeColor = Theme.Danger;
            _feedback.Text = result.error;
        }
        catch (OperationCanceledException)
        {
            // Closing the dialog cannot interrupt LookupAccountName, but it does
            // prevent its eventual continuation from touching disposed controls.
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || IsDisposed || Disposing) return;
            _feedback.ForeColor = Theme.Danger;
            _feedback.Text = ex.Message;
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                UseWaitCursor = false;
                _accountName.Enabled = true;
                _find.Enabled = true;
                _accountName.SelectAll();
                _accountName.Select();
            }
        }
    }
}
