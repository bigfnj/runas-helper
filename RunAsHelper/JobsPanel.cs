using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using RunAsHelper.Core;
using RunAsHelper.Shared.Protocol;

namespace RunAsHelper
{
    /// <summary>
    /// The Active Jobs view: which launches are currently holding a service launch slot,
    /// what they have printed so far, and a way to terminate one that is stuck.
    ///
    /// Context: the service allows a bounded number of concurrent launches. A
    /// fire-and-forget launch releases its slot as soon as the process is created, so in
    /// practice the jobs listed here are <c>/capture</c> launches, which hold their slot
    /// until the child exits or the <c>/timeout</c> ceiling fires. Before this view a
    /// stuck capture job was invisible — the service simply looked unresponsive.
    ///
    /// This lives in the main window as a collapsible right-hand pane (toggled by the
    /// status bar's "Jobs:" label or Tools → Active Jobs) rather than in a dialog, so the
    /// count you clicked and the jobs behind it are on screen together. Polling follows
    /// <see cref="Control.Visible"/>, which is false both while the pane is collapsed and
    /// while the whole window is hidden to the tray — neither state has a reader.
    /// </summary>
    internal sealed class JobsPanel : UserControl
    {
        private readonly PipeClient _client      = new();
        private readonly Label      _header      = new();
        private readonly ListView   _list        = new();
        private readonly Panel      _outputBox   = new();
        private readonly Label      _outputLabel = new();
        private readonly TextBox    _output      = new();
        private readonly Panel      _footer      = new();
        private readonly Label      _slots       = new();
        private readonly Button     _kill        = new();
        private readonly Button     _close       = new();
        private int  _outputForJob = -1;
        private bool _busy;
        private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 2_000 };

        /// <summary>Raised by the pane's Close button; the host collapses the pane.</summary>
        public event EventHandler? CloseRequested;

        public JobsPanel()
        {
            Padding = new Padding(8, 4, 8, 8);

            _header.Dock      = DockStyle.Top;
            _header.Height    = 22;
            _header.Font      = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            _header.TextAlign = ContentAlignment.MiddleLeft;
            _header.Text      = "Active Jobs";

            _list.Dock          = DockStyle.Fill;
            _list.View          = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect   = false;
            _list.HideSelection = false;
            _list.ShowItemToolTips = true;
            _list.Columns.Add("Job", 46);
            _list.Columns.Add("Elapsed", 70);
            _list.Columns.Add("Account", 110);
            _list.Columns.Add("Source", 60);
            _list.Columns.Add("PID", 60);
            _list.Columns.Add("Command", 300);
            _list.SelectedIndexChanged += async (_, _) => { UpdateButtons(); await LoadOutputAsync(); };
            _list.Resize += (_, _) => StretchCommandColumn();

            // Output box: what the selected job has actually printed so far. A command
            // line tells you what was asked for; this tells you where it got stuck.
            _outputLabel.Dock      = DockStyle.Top;
            _outputLabel.Height    = 18;
            _outputLabel.TextAlign = ContentAlignment.MiddleLeft;
            _outputLabel.Text      = "Captured output (select a job):";

            _output.Dock       = DockStyle.Fill;
            _output.Multiline  = true;
            _output.ReadOnly   = true;
            _output.ScrollBars = ScrollBars.Vertical;
            _output.WordWrap   = false;
            _output.Font       = new Font(FontFamily.GenericMonospace, 8.5f);

            _outputBox.Dock    = DockStyle.Bottom;
            _outputBox.Height  = 168;
            _outputBox.Padding = new Padding(0, 6, 0, 0);
            _outputBox.Controls.Add(_output);        // Fill first...
            _outputBox.Controls.Add(_outputLabel);   // ...edge last

            _slots.Dock      = DockStyle.Left;
            _slots.Width     = 250;
            _slots.TextAlign = ContentAlignment.MiddleLeft;
            _slots.Text      = "Slots in use: —";

            _kill.Size    = new Size(90, 28);
            _kill.Text    = "Kill";
            _kill.Enabled = false;
            _kill.Click  += async (_, _) => await KillSelectedAsync();

            _close.Size   = new Size(90, 28);
            _close.Text   = "Close";
            _close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

            _footer.Dock    = DockStyle.Bottom;
            _footer.Height  = 36;
            _footer.Padding = new Padding(0, 6, 0, 0);
            _footer.Controls.AddRange([_slots, _kill, _close]);
            _footer.SizeChanged += (_, _) => LayoutFooter();

            Controls.Add(_list);        // Fill first...
            Controls.Add(_outputBox);
            Controls.Add(_footer);
            Controls.Add(_header);      // ...edges outermost last

            _refresh.Tick += async (_, _) => await ReloadAsync();
        }

        // The command line is the one open-ended column, so it absorbs the leftover
        // width — the pane is narrow enough that fixed columns would otherwise leave a
        // horizontal scrollbar under the list at every size.
        private void StretchCommandColumn()
        {
            if (_list.Columns.Count < 6) return;
            int fixedW = 0;
            for (int i = 0; i < _list.Columns.Count - 1; i++) fixedW += _list.Columns[i].Width;
            _list.Columns[^1].Width = Math.Max(LogicalToDeviceUnits(120),
                                               _list.ClientSize.Width - fixedW - 4);
        }

        // Places the two buttons at the footer's right edge. Driven explicitly rather
        // than by a Top|Right anchor for the same reason as MainForm's quick-run path
        // box: under AutoScaleMode.Font the anchored right margin did not hold, and
        // this panel is resized twice over — by the window and by the pane splitter.
        private void LayoutFooter()
        {
            int top = _footer.Padding.Top;
            _close.Location = new Point(_footer.ClientSize.Width - _close.Width, top);
            _kill.Location  = new Point(_close.Left - _kill.Width - LogicalToDeviceUnits(8), top);
        }

        // Polling follows effective visibility: collapsing the pane or hiding the window
        // to the tray both stop it, and re-showing reloads at once rather than leaving a
        // stale snapshot up for a whole refresh interval.
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                LayoutFooter();
                StretchCommandColumn();
                _refresh.Start();
                _ = ReloadAsync();
            }
            else
            {
                _refresh.Stop();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _refresh.Stop();
                _refresh.Dispose();
            }
            base.Dispose(disposing);
        }

        private async Task ReloadAsync()
        {
            // The service only answers the job verbs for the installed, elevated tray,
            // so say why the pane is empty instead of showing a blank list that looks
            // broken — and skip a round-trip that would fail anyway.
            if (!NativeMethods.IsUserAnAdmin())
            {
                _list.Items.Clear();
                _slots.Text = "Needs an elevated tray — click Activate.";
                UpdateButtons();
                return;
            }

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

                await LoadOutputAsync();

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

        // Pulls the selected job's captured tail. Only capture jobs produce output, so
        // say so explicitly rather than leaving an empty box that looks like a failure.
        private async Task LoadOutputAsync()
        {
            int id = SelectedJobId();
            if (id < 0)
            {
                _outputForJob = -1;
                _outputLabel.Text = "Captured output (select a job):";
                if (_output.Text.Length > 0) _output.Clear();
                return;
            }

            var job = (JobInfo?)(_list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag : null);
            _outputLabel.Text = $"Captured output — job {id}:";

            if (job is not null && !job.CaptureOutput)
            {
                _outputForJob = id;
                _output.Text  = "(this job was not launched with /capture, so no output is streamed)";
                return;
            }

            try
            {
                var lines = await _client.JobOutputAsync(id);
                if (IsDisposed || !IsHandleCreated || SelectedJobId() != id) return;

                string text = lines.Count == 0
                    ? "(no output captured yet)"
                    : string.Join(Environment.NewLine, lines);
                if (_output.Text != text)
                {
                    bool atEnd = _outputForJob != id || _output.SelectionStart >= _output.TextLength - 2;
                    _output.Text = text;
                    if (atEnd) { _output.SelectionStart = _output.TextLength; _output.ScrollToCaret(); }
                }
                _outputForJob = id;
            }
            catch { /* best-effort, same as the listing */ }
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
