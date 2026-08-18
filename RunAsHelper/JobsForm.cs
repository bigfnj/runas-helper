using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using RunAsHelper.Core;
using RunAsHelper.Shared.Protocol;

namespace RunAsHelper
{
    /// <summary>
    /// Shows the launches currently holding a service launch slot, and lets an operator
    /// terminate one that is stuck.
    ///
    /// Context: the service allows a bounded number of concurrent launches. A
    /// fire-and-forget launch releases its slot as soon as the process is created, so in
    /// practice the jobs listed here are <c>/capture</c> launches, which hold their slot
    /// until the child exits or the <c>/timeout</c> ceiling fires. Before this view a
    /// stuck capture job was invisible — the service simply looked unresponsive.
    /// </summary>
    internal sealed class JobsForm : Form
    {
        private readonly PipeClient _client = new();
        private readonly ListView   _list   = new();
        private readonly Label      _slots  = new();
        private readonly Button     _kill   = new();
        private readonly Button     _close  = new();
        private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 2_000 };
        private bool _busy;

        public JobsForm()
        {
            Text            = "Active Jobs";
            StartPosition   = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            MinimizeBox     = false;
            MaximizeBox     = false;
            ClientSize      = new Size(760, 320);
            MinimumSize     = new Size(560, 240);

            _list.SetBounds(12, 12, ClientSize.Width - 24, ClientSize.Height - 84);
            _list.View          = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect   = false;
            _list.HideSelection = false;
            _list.Anchor        = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _list.Columns.Add("Job", 46);
            _list.Columns.Add("Elapsed", 70);
            _list.Columns.Add("Account", 110);
            _list.Columns.Add("Source", 60);
            _list.Columns.Add("PID", 60);
            _list.Columns.Add("Command", 300);
            _list.SelectedIndexChanged += (_, _) => UpdateButtons();

            _slots.SetBounds(12, ClientSize.Height - 64, 320, 20);
            _slots.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _slots.Text   = "Slots in use: —";

            _kill.SetBounds(ClientSize.Width - 200, ClientSize.Height - 68, 90, 28);
            _kill.Anchor  = AnchorStyles.Bottom | AnchorStyles.Right;
            _kill.Text    = "Kill";
            _kill.Enabled = false;
            _kill.Click  += async (_, _) => await KillSelectedAsync();

            _close.SetBounds(ClientSize.Width - 102, ClientSize.Height - 68, 90, 28);
            _close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _close.Text   = "Close";
            _close.Click += (_, _) => Close();

            Controls.AddRange([_list, _slots, _kill, _close]);
            CancelButton = _close;

            _refresh.Tick += async (_, _) => await ReloadAsync();
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            await ReloadAsync();
            _refresh.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _refresh.Stop();
            _refresh.Dispose();
            base.OnFormClosed(e);
        }

        private async Task ReloadAsync()
        {
            // Overlapping refreshes would fight over the list; skip a tick if the
            // previous round-trip has not finished.
            if (_busy) return;
            _busy = true;
            try
            {
                var (_, jobs, slots) = await _client.ListJobsAsync();
                if (IsDisposed || !IsHandleCreated) return;

                int selectedId = SelectedJobId();
                _list.BeginUpdate();
                _list.Items.Clear();
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var job in jobs)
                {
                    var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, nowMs - job.StartedUnixMs));
                    var item = new ListViewItem(job.Id.ToString()) { Tag = job };
                    item.SubItems.Add($"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}");
                    item.SubItems.Add(job.Account == "system" ? "SYSTEM" : "TrustedInstaller");
                    item.SubItems.Add(job.Source);
                    item.SubItems.Add(job.Pid == 0 ? "—" : job.Pid.ToString());
                    item.SubItems.Add(job.CommandLine);
                    if (job.CaptureOutput && job.TimeoutSeconds <= 0)
                        item.ToolTipText = "/capture without /timeout — this job holds its slot until the process exits.";
                    _list.Items.Add(item);
                    if (job.Id == selectedId) item.Selected = true;
                }
                _list.EndUpdate();

                _slots.Text = string.IsNullOrEmpty(slots)
                    ? (jobs.Count == 0 ? "No active jobs." : $"{jobs.Count} active job(s).")
                    : $"Slots in use: {slots}";
                UpdateButtons();
            }
            catch
            {
                // Best-effort view: a failed round-trip just leaves the last snapshot up.
            }
            finally { _busy = false; }
        }

        private int SelectedJobId()
            => _list.SelectedItems.Count > 0 ? ((JobInfo)_list.SelectedItems[0].Tag!).Id : -1;

        private void UpdateButtons()
            => _kill.Enabled = _list.SelectedItems.Count > 0;

        private async Task KillSelectedAsync()
        {
            if (_list.SelectedItems.Count == 0) return;
            var job = (JobInfo)_list.SelectedItems[0].Tag!;

            if (MessageBox.Show(this,
                    $"Terminate this elevated process?\n\n{job.CommandLine}\n\nPID {job.Pid}",
                    "RunAS Helper", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                != DialogResult.Yes) return;

            _kill.Enabled = false;
            bool ok = await _client.KillJobAsync(job.Id);
            if (!ok)
                MessageBox.Show(this,
                    "Could not terminate that job — it may have already finished.",
                    "RunAS Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await ReloadAsync();
        }
    }
}
