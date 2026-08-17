using System;
using System.Diagnostics.Eventing.Reader;
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
        private readonly bool _startHidden;
        private bool  _firstShowHandled;

        private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 60_000 };

        // Watches the service's structured events to flag CLI-sourced launches.
        private EventLogWatcher? _cliWatcher;

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

        public MainForm(bool startHidden = false)
        {
            InitializeComponent();
            // Stamp the running version into the title bar so screenshots are
            // unambiguous about exactly which build is on screen.
            Text = $"RunAS Helper - {AppVersionString()}";
            _startHidden = startHidden || _settings.StartMinimized;
            // Set the window icon to power.ico before SetTrayIcon(), which derives
            // the tray icons (grey + colour) from this.Icon. Without this the form
            // falls back to the default WinForms icon.
            if (LoadAppIcon() is { } appIcon) Icon = appIcon;
            WireEvents();
            SetTrayIcon();
            RebuildSavedAppsMenu();
            RefreshAppsList();
        }

        // Start to the tray only: suppress the very first show (no window flash),
        // but force the handle so OnLoad/timers/tray icon still run. The tray-icon
        // click (ShowFromTray) opens the window normally afterwards.
        protected override void SetVisibleCore(bool value)
        {
            if (!_firstShowHandled && _startHidden && value)
            {
                _firstShowHandled = true;
                if (!IsHandleCreated) CreateHandle();
                base.SetVisibleCore(false);
                return;
            }
            _firstShowHandled = true;
            base.SetVisibleCore(value);
        }

        // Loads power.ico (embedded as a manifest resource) for the window/tray icon.
        private static Icon? LoadAppIcon()
        {
            try
            {
                using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("power.ico");
                return stream is null ? null : new Icon(stream);
            }
            catch { return null; }
        }

        // ── Setup ────────────────────────────────────────────────────────────

        private void WireEvents()
        {
            btnRunTI.Click         += async (_, _) => await RunQuickAsync("ti");
            btnRunSystem.Click     += async (_, _) => await RunQuickAsync("system");
            btnBrowse.Click        += BtnBrowse_Click;
            panelTop.SizeChanged   += (_, _) => LayoutQuickRunPath();
            btnAddApp.Click        += (_, _) => AddApplication();
            btnRunSaved.Click      += async (_, _) => await RunSelectedAppAsync();
            btnEditApp.Click       += (_, _) => EditSelectedApp();
            btnRemoveApp.Click     += (_, _) => RemoveSelectedApp();
            btnUpApp.Click         += (_, _) => MoveSelectedApp(-1);
            btnDownApp.Click       += (_, _) => MoveSelectedApp(+1);
            lvApps.SelectedIndexChanged += (_, _) => UpdateAppButtonStates();
            lvApps.DoubleClick     += async (_, _) => await RunSelectedAppAsync();
            lvApps.KeyDown         += LvApps_KeyDown;
            lvApps.Resize          += (_, _) => StretchAppColumns();
            menuShow.Click         += (_, _) => ShowFromTray();
            menuExit.Click         += MenuExit_Click;
            menuStartService.Click += MenuStartService_Click;
            menuSettings.Click     += MenuSettings_Click;
            menuValidate.Click     += MenuValidate_Click;
            menuImport.Click       += MenuImport_Click;
            menuExport.Click       += MenuExport_Click;
            menuClearRecent.Click  += MenuClearRecent_Click;
            menuHowToUse.Click     += (_, _) => { using var f = new HelpForm(); f.ShowDialog(this); };
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

            // Register/unregister the login auto-start (HKCU Run → "exe" --tray).
            ApplyStartupRegistration();

            // Begin watching for command-line-sourced launches (independent of
            // elevation — runs even on the non-elevated tray, so escalations made
            // while the CLI gate is open are surfaced regardless).
            StartCliLaunchMonitor();

            // First launch after an install: run the validation popup — but not on a
            // tray-only (login) start, which would defeat the clean startup.
            if (!_startHidden)
                BeginInvoke(TryShowPendingValidation);

            if (!NativeMethods.IsUserAnAdmin())
            {
                // Not elevated: grey the operational controls and surface a single
                // Activate action that relaunches elevated (via Avecto/UAC). On a
                // standard-user/Avecto machine this is the supported way up — the
                // tray can't be auto-started elevated by the logon task.
                btnRunTI.Enabled = btnRunSystem.Enabled = comboPriority.Enabled =
                    comboPath.Enabled = btnBrowse.Enabled = false;
                btnActivate.Visible  = true;
                menuActivate.Visible = true;
                NativeMethods.SendMessage(btnActivate.Handle, NativeMethods.BCM_SETSHIELD, IntPtr.Zero, new IntPtr(1));
                lblNotAdmin.Text    = "Not elevated — click Activate to elevate with Avecto.";
                lblNotAdmin.Visible = true;
                AppendLog("Not elevated. Click Activate to relaunch elevated (Avecto); the window then becomes fully functional.");
                return;
            }

            NativeMethods.SendMessage(btnRunTI.Handle, NativeMethods.BCM_SETSHIELD, IntPtr.Zero, new IntPtr(1));
            NativeMethods.SendMessage(btnRunSystem.Handle, NativeMethods.BCM_SETSHIELD, IntPtr.Zero, new IntPtr(1));
            AppendLog("Checking RunAS Helper service...");
            CheckServiceStatusAsync();
            _statusTimer.Start();
            // Note: starting to the tray is handled by SetVisibleCore (no window
            // flash), so no HideToTray call is needed here.
        }

        // Size the quick-run path box once the form is fully laid out (and again on
        // every resize, via panelTop.SizeChanged) so it keeps a fixed right margin.
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LayoutQuickRunPath();
        }

        // Sets the quick-run path box width so its right edge keeps a fixed, DPI-scaled
        // margin from the panel's right border, at any window width. Driven explicitly
        // (OnShown + panelTop.SizeChanged) instead of via a Left|Right anchor, which did
        // not reliably hold the margin under AutoScaleMode.Font.
        private void LayoutQuickRunPath()
        {
            if (comboPath is null || panelTop is null) return;
            int w = panelTop.ClientSize.Width - comboPath.Left - LogicalToDeviceUnits(48);
            w = Math.Max(60, w);
            if (comboPath.Width != w) comboPath.Width = w;
        }

        private static string AppVersionString()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "v?" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }

        // Keep the HKCU "Run" entry in sync with the StartWithWindows setting, so
        // the tray launches (icon only) at login. Launches non-elevated with --tray.
        private void ApplyStartupRegistration()
        {
            try
            {
                const string runKey   = @"Software\Microsoft\Windows\CurrentVersion\Run";
                const string valueName = "RunAsHelper";
                using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true)
                                ?? Registry.CurrentUser.CreateSubKey(runKey);
                if (key is null) return;

                if (_settings.StartWithWindows)
                {
                    string exe = Environment.ProcessPath ?? Application.ExecutablePath;
                    key.SetValue(valueName, $"\"{exe}\" --tray");
                }
                else
                {
                    key.DeleteValue(valueName, throwOnMissingValue: false);
                }
            }
            catch { /* best-effort; startup registration is non-critical */ }
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
            try { if (_cliWatcher is not null) { _cliWatcher.Enabled = false; _cliWatcher.Dispose(); } } catch { }
            // Reset the CLI gate to off on exit (best-effort), so the command line
            // isn't left enabled when no tray session is active.
            try { _client.SetCommandLineAllowedAsync(false).Wait(800); } catch { }
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
                // Runs on a thread-pool thread. If the form is torn down between the
                // pipe check and here, BeginInvoke throws (ObjectDisposed/
                // InvalidOperation) with no UI-thread handler to catch it — guard it.
                try
                {
                    if (IsHandleCreated && !IsDisposed)
                        BeginInvoke(() => ApplyServiceState(available));
                }
                catch (ObjectDisposedException)   { /* form closed mid-check */ }
                catch (InvalidOperationException) { /* handle gone mid-check */ }
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
                    btnRunTI.Enabled = btnRunSystem.Enabled = true;
                    lblNotAdmin.Visible = false;
                    PushCliAllowed();   // sync the CLI gate (off on startup = reset)
                }
            }
            else
            {
                notifyIcon.Icon     = _greyIcon ?? this.Icon;
                notifyIcon.Text     = "RunAS Helper  ✗ Service offline";
                AppendLog("RunAS Helper service is not running.");
                btnRunTI.Enabled = btnRunSystem.Enabled = false;
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

        // ── CLI-launch awareness ─────────────────────────────────────────────

        // Subscribes to the service's structured Application-log events (source
        // RunAsHelper, ID 1001 = request received) and raises a tray balloon for any
        // *command-line*-sourced launch. While "Allow command line" is enabled any
        // interactive process can drive the pipe, so this makes an out-of-band
        // escalation the user didn't initiate visible. Best-effort: if the log can't
        // be subscribed to, notifications are simply skipped.
        private void StartCliLaunchMonitor()
        {
            try
            {
                var query = new EventLogQuery("Application", PathType.LogName,
                    "*[System[Provider[@Name='RunAsHelper'] and (EventID=1001)]]");
                _cliWatcher = new EventLogWatcher(query);
                _cliWatcher.EventRecordWritten += OnRunAsHelperEvent;
                _cliWatcher.Enabled = true;
            }
            catch
            {
                _cliWatcher = null;
            }
        }

        private void OnRunAsHelperEvent(object? sender, EventRecordWrittenEventArgs e)
        {
            // Raised on an EventLogWatcher background thread. An exception that
            // escapes here has no UI-thread handler and terminates the process, so
            // the whole body — including EventRecord access and disposal — is
            // guarded, not just the parsing.
            try
            {
                using var record = e.EventRecord;
                if (record is null) return;

                // EventLog.WriteEntry stores the whole message as the first insertion
                // string; read it directly (the source has no message DLL, so
                // FormatDescription() is unreliable here).
                string text = record.Properties.Count > 0
                    ? record.Properties[0].Value?.ToString() ?? string.Empty
                    : string.Empty;
                if (text.IndexOf("Source: cli", StringComparison.OrdinalIgnoreCase) < 0)
                    return;

                string cmd = ExtractFirstQuoted(text);
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(() => ShowCliLaunchToast(cmd));
            }
            catch { /* never let a log-read failure disturb the tray */ }
        }

        private void ShowCliLaunchToast(string commandLine)
        {
            string body = string.IsNullOrEmpty(commandLine)
                ? "A command-line launch was elevated via RunAS Helper."
                : "Command-line launch elevated:\n" +
                  (commandLine.Length > 120 ? commandLine[..117] + "..." : commandLine);
            notifyIcon.ShowBalloonTip(6000, "RunAS Helper — command-line launch",
                body, ToolTipIcon.Warning);
        }

        // Text between the first pair of single quotes — the service logs the target
        // as: Launch requested: '<commandLine>'.
        private static string ExtractFirstQuoted(string s)
        {
            int a = s.IndexOf('\'');
            if (a < 0) return string.Empty;
            int b = s.IndexOf('\'', a + 1);
            return b > a ? s.Substring(a + 1, b - a - 1) : string.Empty;
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
            if (form.ShowDialog(this) == DialogResult.OK)
            {
                PushCliAllowed();
                ApplyStartupRegistration();
            }
        }

        // Push the current "Allow command line" state to the service (the gate).
        // Best-effort: needs an elevated connection; harmless if it can't connect.
        private void PushCliAllowed()
        {
            if (_serviceOnline != true) return;
            _ = _client.SetCommandLineAllowedAsync(_settings.AllowCommandLine);
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
                {
                    var a = app;
                    // Migrate legacy exports that used a single CommandLine field.
                    if (string.IsNullOrEmpty(a.Location) && !string.IsNullOrEmpty(a.CommandLine))
                    {
                        var (loc, param) = SavedApplication.SplitCommandLine(a.CommandLine!);
                        a = a with { Location = loc, Parameter = param, CommandLine = null };
                    }
                    _settings.SaveApp(a);
                }

                _settings.Save();
                RefreshAppsList();
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
                more.Click += (_, _) => ShowFromTray();
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

                bool ok = await _client.LaunchElevatedAsync(
                    app.EffectiveCommandLine, app.Priority, app.WorkingDirectory, app.ShowWindow, app.Account);

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

        // ── Saved-apps list (main surface) ───────────────────────────────────

        private void RefreshAppsList()
        {
            int sel = SelectedAppIndex();
            lvApps.BeginUpdate();
            lvApps.Items.Clear();
            foreach (var app in _settings.SavedApplications)
            {
                var item = new ListViewItem(app.Name) { Tag = app };
                item.SubItems.Add(app.Location);
                item.SubItems.Add(app.Parameter);
                lvApps.Items.Add(item);
            }
            lvApps.EndUpdate();

            if (sel >= 0 && sel < lvApps.Items.Count)
            {
                lvApps.Items[sel].Selected = true;
                lvApps.Items[sel].EnsureVisible();
            }
            StretchAppColumns();
            UpdateAppButtonStates();
        }

        // Stretch the "File Location" column to fill the list's free width.
        private void StretchAppColumns()
        {
            if (lvApps.Columns.Count < 3) return;
            int fixedW = lvApps.Columns[0].Width + lvApps.Columns[2].Width;
            int avail  = lvApps.ClientSize.Width - fixedW - 4;
            lvApps.Columns[1].Width = Math.Max(160, avail);
        }

        private void UpdateAppButtonStates()
        {
            bool has = lvApps.SelectedItems.Count > 0;
            int  idx = has ? lvApps.SelectedItems[0].Index : -1;
            btnRunSaved.Enabled  = has;
            btnEditApp.Enabled   = has;
            btnRemoveApp.Enabled = has;
            btnUpApp.Enabled     = has && idx > 0;
            btnDownApp.Enabled   = has && idx < lvApps.Items.Count - 1;
        }

        private SavedApplication? SelectedApp()
            => lvApps.SelectedItems.Count > 0 ? (SavedApplication)lvApps.SelectedItems[0].Tag! : null;

        private int SelectedAppIndex()
            => lvApps.SelectedItems.Count > 0 ? lvApps.SelectedItems[0].Index : -1;

        private void SelectAppByName(string name)
        {
            foreach (ListViewItem it in lvApps.Items)
                if (string.Equals(((SavedApplication)it.Tag!).Name, name, StringComparison.OrdinalIgnoreCase))
                { it.Selected = true; it.EnsureVisible(); break; }
        }

        private void AddApplication()
        {
            using var dlg = new ItemEditForm(null);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            _settings.SaveApp(dlg.Result);
            RefreshAppsList();
            RebuildSavedAppsMenu();
            SelectAppByName(dlg.Result.Name);
            AppendLog($"Saved \"{dlg.Result.Name}\".");
            if (dlg.RunAfterSave) _ = LaunchSavedAppAsync(dlg.Result);
        }

        private void EditSelectedApp()
        {
            var app = SelectedApp();
            if (app is null) return;
            string originalName = app.Name;

            using var dlg = new ItemEditForm(app);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            // SaveApp matches by name; if renamed, drop the old entry first.
            if (!string.Equals(originalName, dlg.Result.Name, StringComparison.OrdinalIgnoreCase))
                _settings.RemoveSavedApp(originalName);
            _settings.SaveApp(dlg.Result);
            RefreshAppsList();
            RebuildSavedAppsMenu();
            SelectAppByName(dlg.Result.Name);
            if (dlg.RunAfterSave) _ = LaunchSavedAppAsync(dlg.Result);
        }

        private void RemoveSelectedApp()
        {
            var app = SelectedApp();
            if (app is null) return;
            if (MessageBox.Show(this, $"Remove \"{app.Name}\"?", "RunAS Helper",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            _settings.RemoveSavedApp(app.Name);
            RefreshAppsList();
            RebuildSavedAppsMenu();
        }

        private void MoveSelectedApp(int delta)
        {
            int idx = SelectedAppIndex();
            if (idx < 0) return;
            int newIdx = idx + delta;
            if (newIdx < 0 || newIdx >= _settings.SavedApplications.Count) return;

            _settings.MoveSavedApp(idx, newIdx);
            RefreshAppsList();
            RebuildSavedAppsMenu();
            if (newIdx < lvApps.Items.Count)
            {
                lvApps.Items[newIdx].Selected = true;
                lvApps.Items[newIdx].EnsureVisible();
            }
        }

        private async Task RunSelectedAppAsync()
        {
            var app = SelectedApp();
            if (app is not null) await LaunchSavedAppAsync(app);
        }

        private void LvApps_KeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Delete: RemoveSelectedApp(); break;
                case Keys.F2:     EditSelectedApp();   break;
                case Keys.Enter:  e.SuppressKeyPress = true; _ = RunSelectedAppAsync(); break;
            }
        }

        // ── Quick run (one-off) ──────────────────────────────────────────────

        // Quick-run: launch the typed/browsed path under the given account ("ti" or
        // "system"). Called by the two account-specific run buttons — the button you
        // click selects the account, so there is no separate account dropdown.
        private async Task RunQuickAsync(string account)
        {
            string path = comboPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                AppendLog("Enter a path first.");
                return;
            }

            string acctName = account == "system" ? "SYSTEM" : "TrustedInstaller";
            btnRunTI.Enabled = btnRunSystem.Enabled = false;
            try
            {
                uint priority = PriorityClasses[comboPriority.SelectedIndex];
                bool ok = await _client.LaunchElevatedAsync(path, priority, "", 1 /* SW_SHOWNORMAL */, account);

                _settings.PriorityIndex = comboPriority.SelectedIndex;
                _settings.AddMru(path);
                RefreshMruCombo();

                AppendLog(ok ? $"Launch succeeded (as {acctName})." : $"Launch failed (as {acctName}).");
            }
            finally
            {
                btnRunTI.Enabled = btnRunSystem.Enabled = true;
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
