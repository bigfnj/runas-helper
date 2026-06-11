using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
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
        private bool? _serviceOnline;

        private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 60_000 };

        private const int TrayMenuCap   = 7;
        private const int RecentMenuCap = 5;

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
            RebuildSavedAppsMenu();
        }

        // ── Setup ────────────────────────────────────────────────────────────

        private void WireEvents()
        {
            btnRun.Click           += BtnRun_Click;
            btnSave.Click          += BtnSave_Click;
            btnBrowse.Click        += BtnBrowse_Click;
            menuShow.Click         += (_, _) => ShowFromTray();
            menuExit.Click         += MenuExit_Click;
            menuStartService.Click += MenuStartService_Click;
            menuSettings.Click     += MenuSettings_Click;
            menuValidate.Click     += MenuValidate_Click;
            menuImport.Click       += MenuImport_Click;
            menuExport.Click       += MenuExport_Click;
            menuClearRecent.Click  += MenuClearRecent_Click;
            menuOpenPwsh.Click     += MenuOpenPwsh_Click;
            menuToolsOpenPwsh.Click += MenuOpenPwsh_Click;
            btnActivate.Click      += (_, _) => ActivateElevation();
            menuActivate.Click     += (_, _) => ActivateElevation();
            notifyIcon.Click       += (_, _) => ShowFromTray();
            notifyIcon.DoubleClick += (_, _) => ShowFromTray();
            trayMenu.Opening       += (_, _) => { RebuildSavedAppsMenu(); RebuildRecentMenu(); };
            _client.LogMessage     += AppendLog;
            _statusTimer.Tick      += (_, _) => CheckServiceStatusAsync();
        }

        private void SetTrayIcon()
        {
            _greyIcon = MakeGreyscaleIcon(this.Icon);
            notifyIcon.Icon = _greyIcon ?? this.Icon;
        }

        private static Icon? MakeGreyscaleIcon(Icon? source)
        {
            if (source is null) return null;
            try
            {
                using var smallIcon = new Icon(source, SystemInformation.SmallIconSize);
                using var src       = smallIcon.ToBitmap();
                using var dst       = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                using var g         = Graphics.FromImage(dst);

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

            // First launch after an install: run the validation popup. Queued here
            // (before the non-admin early-return) so its "Restart as administrator"
            // recovery is reachable even when the tray was started unelevated.
            BeginInvoke(TryShowPendingValidation);

            if (!NativeMethods.IsUserAnAdmin())
            {
                // Not elevated: grey the operational controls and surface a single
                // Activate action that relaunches elevated (via Avecto/UAC). On a
                // standard-user/Avecto machine this is the supported way up — the
                // tray can't be auto-started elevated by the logon task.
                btnRun.Enabled = comboPriority.Enabled = comboPath.Enabled =
                    btnBrowse.Enabled = btnSave.Enabled = false;
                btnRun.Visible       = false;
                btnActivate.Visible  = true;
                menuActivate.Visible = true;
                NativeMethods.SendMessage(btnActivate.Handle, NativeMethods.BCM_SETSHIELD, IntPtr.Zero, new IntPtr(1));
                lblNotAdmin.Text    = "Not elevated — click Activate to elevate with Avecto.";
                lblNotAdmin.Visible = true;
                AppendLog("Not elevated. Click Activate to relaunch elevated (Avecto); the window then becomes fully functional.");
                return;
            }

            NativeMethods.SendMessage(btnRun.Handle, NativeMethods.BCM_SETSHIELD, IntPtr.Zero, new IntPtr(1));
            AppendLog("Checking RunAS Helper service...");
            CheckServiceStatusAsync();
            _statusTimer.Start();

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
            _statusTimer.Stop();
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
                bool available = NativeMethods.WaitNamedPipeW(@"\\.\pipe\RunAsHelper", 500);
                BeginInvoke(() => ApplyServiceState(available));
            });
        }

        private void ApplyServiceState(bool available)
        {
            if (_serviceOnline == available) return;
            _serviceOnline = available;

            menuStartService.Visible = !available;

            if (available)
            {
                notifyIcon.Icon     = this.Icon;
                notifyIcon.Text     = "RunAS Helper  ✓ Ready";
                AppendLog("RunAS Helper service is running. Ready.");
                if (NativeMethods.IsUserAnAdmin())
                {
                    btnRun.Enabled      = true;
                    lblNotAdmin.Visible = false;
                }
            }
            else
            {
                notifyIcon.Icon     = _greyIcon ?? this.Icon;
                notifyIcon.Text     = "RunAS Helper  ✗ Service offline";
                AppendLog("RunAS Helper service is not running.");
                btnRun.Enabled      = false;
                lblNotAdmin.Text    = "RunAS Helper service is not running.";
                lblNotAdmin.Visible = true;
            }
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

        // ── Tray menu handlers ───────────────────────────────────────────────

        private async void MenuStartService_Click(object? sender, EventArgs e)
        {
            menuStartService.Enabled = false;
            try
            {
                using var proc = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("sc.exe", "start RunASHelper")
                    {
                        CreateNoWindow  = true,
                        UseShellExecute = false,
                    });
                await Task.Delay(3000);
            }
            catch { }
            finally
            {
                menuStartService.Enabled = true;
            }
            CheckServiceStatusAsync();
        }

        private void MenuSettings_Click(object? sender, EventArgs e)
        {
            using var form = new SettingsForm(_settings);
            form.ShowDialog(this);
        }
        private void MenuValidate_Click(object? sender, EventArgs e)
        {
            using var form = new ValidationForm(standalone: false);
            form.ShowDialog(this);
        }

        // Relaunches this app elevated via Avecto/UAC (the "runas" verb). On this
        // standard-user, Avecto-managed machine the tray starts non-elevated; this
        // is the user-initiated, one-prompt elevation. The elevated copy is started
        // with "--activate" so it waits for this instance to release the
        // single-instance mutex before taking over.
        private void ActivateElevation()
        {
            string? exe = Environment.ProcessPath;
            if (exe is null) return;
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(exe, "--activate")
                    {
                        UseShellExecute = true,
                        Verb            = "runas",
                    });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // User declined the Avecto/UAC prompt — stay open so they can retry.
                AppendLog("Elevation was cancelled.");
                return;
            }

            // Hand off: exit so the elevated instance can claim the instance mutex.
            _isExiting = true;
            Application.Exit();
        }

        // Opens an interactive PowerShell as TrustedInstaller (via the service),
        // so the user can keep one elevated shell open and paste commands into it
        // — no per-command UAC. Routed through the same pipe as every other launch.
        private async void MenuOpenPwsh_Click(object? sender, EventArgs e)
        {
            if (!NativeMethods.WaitNamedPipeW(@"\\.\pipe\RunAsHelper", 500))
            {
                if (_settings.ShowTrayNotifications)
                    notifyIcon.ShowBalloonTip(3000, "RunAS Helper",
                        "Service is not running.", ToolTipIcon.Warning);
                AppendLog("Cannot open PowerShell — RunAS Helper service is not running.");
                return;
            }

            AppendLog("Opening PowerShell as TrustedInstaller...");
            bool ok = await _client.LaunchElevatedAsync("powershell.exe", NativeMethods.NORMAL_PRIORITY_CLASS);

            if (_settings.ShowTrayNotifications)
                notifyIcon.ShowBalloonTip(2000, "RunAS Helper",
                    ok ? "PowerShell (TrustedInstaller) launched." : "Failed to open PowerShell.",
                    ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }

        // Shows the validation dialog once after a fresh install. The MSI writes
        // HKLM\Software\RunAsHelper\PendingValidation = <version>; once validated
        // we record that version in HKCU so the popup does not reappear every launch.
        private void TryShowPendingValidation()
        {
            try
            {
                using var hklm = Registry.LocalMachine.OpenSubKey(@"Software\RunAsHelper");
                if (hklm?.GetValue("PendingValidation") is not string pending || pending.Length == 0)
                    return;

                using (var hkcu = Registry.CurrentUser.OpenSubKey(@"Software\RunAsHelper"))
                {
                    if (hkcu?.GetValue("ValidatedVersion") is string done &&
                        string.Equals(done, pending, StringComparison.OrdinalIgnoreCase))
                        return;
                }

                using var form = new ValidationForm(standalone: false);
                form.ShowDialog(this);

                if (form.AllPassed)
                {
                    using var key = Registry.CurrentUser.CreateSubKey(@"Software\RunAsHelper");
                    key?.SetValue("ValidatedVersion", pending);
                }
            }
            catch
            {
                // Registry access is best-effort; never block startup on it.
            }
        }
        private void MenuImport_Click(object? sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog
            {
                Title  = "Import Saved Applications",
                Filter = "JSON files (*.json)|*.json|All Files (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                string json     = File.ReadAllText(dlg.FileName);
                var    imported = JsonSerializer.Deserialize(
                    json, AppSettingsJsonContext.Default.ListSavedApplication);

                if (imported == null || imported.Count == 0)
                {
                    AppendLog("No applications found in the import file.");
                    return;
                }

                foreach (var app in imported)
                    _settings.SaveApp(app);

                _settings.Save();
                RebuildSavedAppsMenu();
                AppendLog($"Imported {imported.Count} application(s).");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed: {ex.Message}", "RunAS Helper",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void MenuExport_Click(object? sender, EventArgs e)
        {
            if (_settings.SavedApplications.Count == 0)
            {
                AppendLog("No saved applications to export.");
                return;
            }

            using var dlg = new SaveFileDialog
            {
                Title      = "Export Saved Applications",
                Filter     = "JSON files (*.json)|*.json|All Files (*.*)|*.*",
                FileName   = "saved-apps.json",
                DefaultExt = "json",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                string json = JsonSerializer.Serialize(
                    _settings.SavedApplications,
                    AppSettingsJsonContext.Default.ListSavedApplication);
                File.WriteAllText(dlg.FileName, json);
                AppendLog($"Exported {_settings.SavedApplications.Count} application(s).");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "RunAS Helper",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void MenuClearRecent_Click(object? sender, EventArgs e)
        {
            _settings.MruList.Clear();
            _settings.Save();
            RefreshMruCombo();
            RebuildRecentMenu();
            AppendLog("Recent history cleared.");
        }

        // ── Recent launches menu ─────────────────────────────────────────────

        private void RebuildRecentMenu()
        {
            menuRecent.DropDownItems.Clear();
            var mru = _settings.MruList;

            if (mru.Count == 0)
            {
                menuRecent.Enabled = false;
                return;
            }

            menuRecent.Enabled = true;
            int shown = Math.Min(mru.Count, RecentMenuCap);
            for (int i = 0; i < shown; i++)
            {
                string entry = mru[i];
                string label = entry.Length > 60 ? "..." + entry[^57..] : entry;
                var item = new ToolStripMenuItem(label) { Tag = entry };
                item.Click += async (_, _) =>
                {
                    if (!NativeMethods.WaitNamedPipeW(@"\\.\pipe\RunAsHelper", 500))
                    {
                        if (_settings.ShowTrayNotifications)
                            notifyIcon.ShowBalloonTip(3000, "RunAS Helper",
                                "Service is not running.", ToolTipIcon.Warning);
                        return;
                    }
                    await _client.LaunchElevatedAsync(entry, NativeMethods.NORMAL_PRIORITY_CLASS);
                };
                menuRecent.DropDownItems.Add(item);
            }
        }

        // ── Saved applications ───────────────────────────────────────────────

        private void RebuildSavedAppsMenu()
        {
            menuSavedApps.DropDownItems.Clear();
            var apps = _settings.SavedApplications;

            if (apps.Count == 0)
            {
                menuSavedApps.Enabled = false;
                return;
            }

            menuSavedApps.Enabled = true;
            int shown = Math.Min(apps.Count, TrayMenuCap);
            for (int i = 0; i < shown; i++)
            {
                var app  = apps[i];
                var item = new ToolStripMenuItem(app.Name);
                item.Click += async (_, _) => await LaunchSavedAppAsync(app);
                menuSavedApps.DropDownItems.Add(item);
            }

            if (apps.Count > TrayMenuCap)
            {
                menuSavedApps.DropDownItems.Add(new ToolStripSeparator());
                var more = new ToolStripMenuItem("More...");
                more.Click += (_, _) => OpenSavedAppsManager();
                menuSavedApps.DropDownItems.Add(more);
            }
        }

        private async Task LaunchSavedAppAsync(SavedApplication app)
        {
            try
            {
                if (!NativeMethods.WaitNamedPipeW(@"\\.\pipe\RunAsHelper", 500))
                {
                    notifyIcon.ShowBalloonTip(3000, "RunAS Helper",
                        "Service is not running.", ToolTipIcon.Warning);
                    return;
                }

                bool ok = await _client.LaunchElevatedAsync(app.CommandLine, app.Priority);

                if (_settings.ShowTrayNotifications)
                    notifyIcon.ShowBalloonTip(2000, "RunAS Helper",
                        ok ? $"{app.Name} launched." : $"Failed to launch {app.Name}.",
                        ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
            }
            catch (Exception ex)
            {
                AppendLog($"Launch error: {ex.Message}");
            }
        }

        private void OpenSavedAppsManager()
        {
            using var form = new SavedAppsForm(_settings, _client);
            form.ShowDialog(this);
            RebuildSavedAppsMenu();
        }

        // ── Button handlers ──────────────────────────────────────────────────

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            string path = comboPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                AppendLog("Enter a path to save.");
                return;
            }

            string defaultName = Path.GetFileNameWithoutExtension(path.Trim('"', ' '));
            if (string.IsNullOrWhiteSpace(defaultName))
                defaultName = path;

            using var prompt = new NamePromptForm(defaultName);
            if (prompt.ShowDialog(this) != DialogResult.OK) return;

            string name = prompt.EnteredName.Trim();
            if (string.IsNullOrWhiteSpace(name)) return;

            uint priority = PriorityClasses[comboPriority.SelectedIndex];
            _settings.SaveApp(new SavedApplication(name, path, priority));
            AppendLog($"Saved \"{name}\".");
            RebuildSavedAppsMenu();
        }

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
            // Activity logging is user-toggleable (Settings → Enable activity
            // logging, on by default). When off, the main-window log stays quiet.
            // The Validate Installation dialog has its own always-on Details pane.
            if (!_settings.EnableLogging) return;

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
