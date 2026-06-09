using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using System.Windows.Forms;
using RunAsHelper.Core;
using RunAsHelper.Settings;

namespace RunAsHelper
{
    internal partial class MainForm : Form
    {
        private readonly PipeClient  _client   = new();
        private readonly AppSettings _settings = AppSettings.Load();
        private bool  _isExiting;
        private Icon? _greyIcon;

        private static readonly uint[] PriorityClasses =
        {
            NativeMethods.IDLE_PRIORITY_CLASS,
            NativeMethods.BELOW_NORMAL_PRIORITY_CLASS,
            NativeMethods.NORMAL_PRIORITY_CLASS,
            NativeMethods.ABOVE_NORMAL_PRIORITY_CLASS,
            NativeMethods.HIGH_PRIORITY_CLASS,
            NativeMethods.REALTIME_PRIORITY_CLASS,
        };

        public MainForm()
        {
            InitializeComponent();
            WireEvents();
            SetTrayIcon();
        }

        // ── Setup ────────────────────────────────────────────────────────────

        private void WireEvents()
        {
            btnRun.Click           += BtnRun_Click;
            btnBrowse.Click        += BtnBrowse_Click;
            menuShow.Click         += (_, _) => ShowFromTray();
            menuExit.Click         += MenuExit_Click;
            notifyIcon.DoubleClick += (_, _) => ShowFromTray();
            _client.LogMessage     += AppendLog;
        }

        private void SetTrayIcon()
        {
            _greyIcon = MakeGreyscaleIcon(this.Icon);
            // Start grey — CheckServiceStatusAsync will switch to full colour if the
            // service pipe is reachable.
            notifyIcon.Icon = _greyIcon ?? this.Icon;
        }

        private static Icon? MakeGreyscaleIcon(Icon source)
        {
            try
            {
                // Extract at the size the OS uses for tray icons so the result is crisp.
                using var smallIcon = new Icon(source, SystemInformation.SmallIconSize);
                using var src       = smallIcon.ToBitmap();
                using var dst       = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                using var g         = Graphics.FromImage(dst);

                // Luminance-preserving desaturation matrix (Rec. 601 weights).
                var matrix = new ColorMatrix(new[]
                {
                    new float[] { 0.299f, 0.299f, 0.299f, 0,  0 },
                    new float[] { 0.587f, 0.587f, 0.587f, 0,  0 },
                    new float[] { 0.114f, 0.114f, 0.114f, 0,  0 },
                    new float[] { 0,      0,      0,      1,  0 },
                    new float[] { 0,      0,      0,      0,  1 },
                });
                using var attr = new ImageAttributes();
                attr.SetColorMatrix(matrix);

                g.DrawImage(src,
                    new Rectangle(0, 0, src.Width, src.Height),
                    0, 0, src.Width, src.Height,
                    GraphicsUnit.Pixel, attr);

                IntPtr hIcon = dst.GetHicon();
                var icon = (Icon)Icon.FromHandle(hIcon).Clone();
                NativeMethods.DestroyIcon(hIcon);
                return icon;
            }
            catch { return null; }
        }

        // ── Form lifecycle ───────────────────────────────────────────────────

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            comboPriority.SelectedIndex = Math.Clamp(_settings.PriorityIndex, 0, comboPriority.Items.Count - 1);
            RefreshMruCombo();

            if (!NativeMethods.IsUserAnAdmin())
            {
                btnRun.Enabled = comboPriority.Enabled = comboPath.Enabled = btnBrowse.Enabled = false;
                lblNotAdmin.Text    = "Restart as Administrator to use RunAsHelper.";
                lblNotAdmin.Visible = true;
                AppendLog("Run as Administrator to connect to the RunAsHelper service.");
                return;
            }

            // Add UAC shield glyph to the run button.
            NativeMethods.SendMessage(btnRun.Handle, NativeMethods.BCM_SETSHIELD, IntPtr.Zero, new IntPtr(1));
            AppendLog("Checking RunAsHelper service...");
            CheckServiceStatusAsync();

            if (_settings.StartMinimized)
                BeginInvoke(HideToTray);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_isExiting && _settings.MinimizeToTray
                && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            notifyIcon.Visible = false;
            base.OnFormClosing(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray)
                HideToTray();
        }

        // ── Service status ───────────────────────────────────────────────────

        private void CheckServiceStatusAsync()
        {
            Task.Run(() =>
            {
                // WaitNamedPipeW probes the pipe without consuming a connection slot.
                bool available = NativeMethods.WaitNamedPipeW(@"\\.\pipe\RunAsHelper", 500);
                BeginInvoke(() =>
                {
                    if (available)
                    {
                        notifyIcon.Icon = this.Icon;   // full colour — service is up
                        AppendLog("RunAsHelper service is running. Ready.");
                        notifyIcon.Text = "RunAS Helper  ✓ Ready";
                    }
                    else
                    {
                        notifyIcon.Icon = _greyIcon ?? this.Icon;  // grey — service is down
                        AppendLog("RunAsHelper service is not running. Install and start the service.");
                        notifyIcon.Text = "RunAS Helper  ✗ Service offline";
                        btnRun.Enabled      = false;
                        lblNotAdmin.Text    = "RunAsHelper service is not running.";
                        lblNotAdmin.Visible = true;
                    }
                });
            });
        }

        // ── Tray helpers ─────────────────────────────────────────────────────

        private void HideToTray()
        {
            Hide();
            if (_settings.ShowTrayNotifications)
                notifyIcon.ShowBalloonTip(2000, "RunAS Helper",
                    "Still running in the system tray.", ToolTipIcon.Info);
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void MenuExit_Click(object? sender, EventArgs e)
        {
            _isExiting = true;
            Application.Exit();
        }

        // ── Button handlers ──────────────────────────────────────────────────

        private async void BtnRun_Click(object? sender, EventArgs e)
        {
            string path = comboPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                AppendLog("Enter a path first.");
                return;
            }

            btnRun.Enabled = false;
            try
            {
                uint priority = PriorityClasses[comboPriority.SelectedIndex];
                bool ok = await _client.LaunchElevatedAsync(path, priority);

                _settings.PriorityIndex = comboPriority.SelectedIndex;
                _settings.AddMru(path);
                RefreshMruCombo();

                AppendLog(ok ? "Launch succeeded." : "Launch failed.");
            }
            finally
            {
                btnRun.Enabled = true;
            }
        }

        private void BtnBrowse_Click(object? sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog
            {
                Title  = "Select program to run elevated",
                Filter = "Programs (*.exe;*.com;*.bat)|*.exe;*.com;*.bat|All Files (*.*)|*.*",
            };

            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                string path = dlg.FileName;
                if (path.Contains(' '))
                    path = $"\"{path}\"";
                comboPath.Text = path;
            }
        }

        // ── Log ──────────────────────────────────────────────────────────────

        private void AppendLog(string message)
        {
            if (txtLog.InvokeRequired)
            {
                txtLog.Invoke(() => AppendLog(message));
                return;
            }
            txtLog.AppendText(message + Environment.NewLine);
            txtLog.ScrollToCaret();
        }

        // ── MRU helpers ──────────────────────────────────────────────────────

        private void RefreshMruCombo()
        {
            string current = comboPath.Text;
            comboPath.Items.Clear();
            foreach (string item in _settings.MruList)
                comboPath.Items.Add(item);
            comboPath.Text = current;
        }
    }
}
