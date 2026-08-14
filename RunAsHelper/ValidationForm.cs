using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RunAsHelper.Core;

namespace RunAsHelper
{
    /// <summary>
    /// Post-install validation dialog. Runs three checks — service running,
    /// tray app running, and a TrustedInstaller token round-trip — and offers
    /// recovery actions (retry, restart elevated, repair, uninstall) when
    /// something is wrong.
    /// </summary>
    internal sealed class ValidationForm : Form
    {
        // Keep in sync with RunAsHelper.Installer/Package.wxs <Package UpgradeCode>.
        private const string UpgradeCode      = "{E5F60718-C9DA-1234-EF01-5F6071829304}";
        private const string PipePath          = @"\\.\pipe\RunAsHelper";

        private readonly PipeClient _client = new();
        private readonly bool       _standalone;
        private string              _tokenDetail = string.Empty;
        private string              _lastServiceLog = string.Empty;
        private bool                _running;

        private readonly CheckRow _svcRow    = new("RunAS Helper service is running");
        private readonly CheckRow _trayRow   = new("Tray application running");
        private readonly CheckRow _tokenRow  = new("TrustedInstaller token acquired & released");
        private readonly CheckRow _systemRow = new("SYSTEM token acquired & released");

        private readonly Label   _summary;
        private readonly TextBox _log;
        private readonly Button _btnRetry;
        private readonly Button _btnElevate;
        private readonly Button _btnRepair;
        private readonly Button _btnUninstall;
        private readonly Button _btnClose;

        /// <summary>True once every check has passed.</summary>
        public bool AllPassed { get; private set; }

        public ValidationForm(bool standalone)
        {
            _standalone = standalone;
            _client.LogMessage += OnServiceLog;

            // ── Window ────────────────────────────────────────────────────────
            Text            = "RunAS Helper — Installation Check";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterScreen;
            ClientSize      = new Size(520, 612);
            AutoScaleMode   = AutoScaleMode.Font;
            try { Icon = new Icon(SystemIcons.Shield, 32, 32); } catch { /* non-fatal */ }

            // ── Header ────────────────────────────────────────────────────────
            var header = new Label
            {
                Dock      = DockStyle.Top,
                Height    = 52,
                Padding   = new Padding(14, 12, 14, 0),
                Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
                Text      = "Verifying your RunAS Helper installation…",
            };

            // ── Buttons (bottom, right-aligned) ───────────────────────────────
            _btnRetry     = MakeButton("Retry",                    (_, _) => _ = RunChecksAsync());
            _btnElevate   = MakeButton("Restart as administrator", (_, _) => RestartElevated());
            _btnRepair    = MakeButton("Repair",                   (_, _) => RunInstaller(repair: true));
            _btnUninstall = MakeButton("Uninstall",                (_, _) => RunInstaller(repair: false));
            _btnClose     = MakeButton("Close",                    (_, _) => Close());

            var buttons = new FlowLayoutPanel
            {
                Dock          = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height        = 52,
                Padding       = new Padding(10, 8, 10, 8),
            };
            // Added right-to-left: Close sits furthest right.
            buttons.Controls.AddRange(new Control[]
            {
                _btnClose, _btnUninstall, _btnRepair, _btnElevate, _btnRetry,
            });

            // ── Summary line (above buttons) ──────────────────────────────────
            _summary = new Label
            {
                Dock      = DockStyle.Bottom,
                Height    = 30,
                Padding   = new Padding(14, 4, 14, 0),
                Font      = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Text      = "Running checks…",
                ForeColor = Color.DimGray,
            };

            // ── Details / live service log (verbatim service output) ──────────
            // Shows every line the service streams, so the full TrustedInstaller
            // token result (account + SID) and any failure reason are visible
            // without truncation — the per-row detail labels are space-limited.
            _log = new TextBox
            {
                Dock       = DockStyle.Bottom,
                Height     = 150,
                Multiline  = true,
                ReadOnly   = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap   = false,
                BackColor  = SystemColors.Window,
                Font       = new Font("Consolas", 8.25f),
            };
            var logLabel = new Label
            {
                Dock      = DockStyle.Bottom,
                Height    = 20,
                Padding   = new Padding(14, 2, 14, 0),
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.DimGray,
                Text      = "Details",
            };

            // ── Check rows (fill) ─────────────────────────────────────────────
            var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 6, 14, 6) };
            int y = 6;
            foreach (var row in new[] { _svcRow, _trayRow, _tokenRow, _systemRow })
            {
                row.AddTo(content, y);
                y += CheckRow.RowHeight;
            }

            // Bottom-docked controls stack with the earliest-added highest, so
            // add top-to-bottom: details label, log box, summary, buttons.
            Controls.Add(content);
            Controls.Add(logLabel);
            Controls.Add(_log);
            Controls.Add(_summary);
            Controls.Add(buttons);
            Controls.Add(header);

            Load += async (_, _) => await RunChecksAsync();
        }

        // ── Check orchestration ───────────────────────────────────────────────

        private async Task RunChecksAsync()
        {
            if (_running) return;
            _running = true;
            try
            {
                SetButtons(retry: false, elevate: false, repair: false, uninstall: false, close: false);
                _summary.ForeColor = Color.DimGray;
                _summary.Text      = "Running checks…";
                _log.Clear();

                bool admin = NativeMethods.IsUserAnAdmin();

                // 1) Service running — proven by the pipe being available. Allow
                //    time for the service to finish starting right after install.
                _svcRow.SetRunning();
                bool svc = await Task.Run(() => WaitForPipe(TimeSpan.FromSeconds(12)));
                _svcRow.SetResult(svc, svc
                    ? "Responding on " + PipePath
                    : "No response from the service pipe");

                // 2) Tray application running (this process holds the instance mutex).
                _trayRow.SetRunning();
                bool tray = IsTrayRunning();
                _trayRow.SetResult(tray, tray
                    ? $"Running (PID {Environment.ProcessId})"
                    : "No running tray instance detected");

                // 3) TrustedInstaller token round-trip via the service.
                _tokenRow.SetRunning();
                bool token;
                if (!admin)
                {
                    token = false;
                    _tokenRow.SetResult(false, "Requires administrator — use “Restart as administrator”");
                }
                else if (!svc)
                {
                    token = false;
                    _tokenRow.SetResult(false, "Skipped — service is not responding");
                }
                else
                {
                    _tokenDetail    = string.Empty;
                    _lastServiceLog = string.Empty;
                    token = await _client.ValidateTokenAsync();
                    _tokenRow.SetResult(token, token
                        ? (_tokenDetail.Length > 0 ? _tokenDetail : "Token acquired and released cleanly")
                        : (_lastServiceLog.Length > 0
                            ? "Failed: " + _lastServiceLog
                            : "Could not acquire/validate the TrustedInstaller token"));
                }

                // 4) SYSTEM token round-trip — confirms the account=”system” path works.
                _systemRow.SetRunning();
                bool systemToken;
                if (!admin)
                {
                    systemToken = false;
                    _systemRow.SetResult(false, "Requires administrator — use “Restart as administrator”");
                }
                else if (!svc)
                {
                    systemToken = false;
                    _systemRow.SetResult(false, "Skipped — service is not responding");
                }
                else
                {
                    _lastServiceLog = string.Empty;
                    systemToken = await _client.ValidateSystemTokenAsync();
                    _systemRow.SetResult(systemToken, systemToken
                        ? "SYSTEM token acquired and released cleanly"
                        : (_lastServiceLog.Length > 0
                            ? "Failed: " + _lastServiceLog
                            : "Could not acquire/validate the SYSTEM token"));
                }

                AllPassed = svc && tray && token && systemToken;
                ShowOutcome(admin);
            }
            finally
            {
                _running = false;
            }
        }

        private void ShowOutcome(bool admin)
        {
            if (AllPassed)
            {
                _summary.ForeColor = Color.SeaGreen;
                _summary.Text      = "✓  All checks passed — RunAS Helper is installed and working.";
                _btnClose.Text     = "Done";
                SetButtons(retry: false, elevate: false, repair: false, uninstall: false, close: true);
                AcceptButton = _btnClose;
            }
            else
            {
                _summary.ForeColor = Color.Firebrick;
                _summary.Text      = "✗  Some checks failed. Choose a recovery action below.";
                _btnClose.Text     = "Close";
                SetButtons(retry: true, elevate: !admin, repair: true, uninstall: true, close: true);
                AcceptButton = _btnRetry;
            }
        }

        // ── Individual checks ─────────────────────────────────────────────────

        private static bool WaitForPipe(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            do
            {
                if (NativeMethods.WaitNamedPipeW(PipePath, 500)) return true;
                Thread.Sleep(500);
            }
            while (DateTime.UtcNow < deadline);
            return false;
        }

        private static bool IsTrayRunning()
        {
            try
            {
                using var existing = Mutex.OpenExisting(AppInstance.MutexName);
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // The mutex exists but is owned in another security context — still "running".
                return true;
            }
        }

        private void OnServiceLog(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                // Remember the most recent streamed line so a token failure can
                // show the exact step the service stopped on, instead of a
                // generic "could not acquire" message.
                _lastServiceLog = message.Trim();

            // Capture the resolved account line, then prefer the final
            // "Validation OK" explanation as the token row's success detail.
            if (message.StartsWith("Token user:", StringComparison.OrdinalIgnoreCase))
                _tokenDetail = message;
            if (message.StartsWith("Validation OK", StringComparison.OrdinalIgnoreCase))
                _tokenDetail = message;

            // Mirror every streamed line into the Details pane, verbatim.
            AppendDetail(message);
        }

        private void AppendDetail(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (_log.IsHandleCreated && _log.InvokeRequired)
            {
                _log.BeginInvoke(() => AppendDetail(message));
                return;
            }
            _log.AppendText(message + Environment.NewLine);
        }

        // ── Recovery actions ──────────────────────────────────────────────────

        private void RestartElevated()
        {
            string? exe = Environment.ProcessPath;
            if (exe is null) return;
            try
            {
                Process.Start(new ProcessStartInfo(exe, "--revalidate")
                {
                    UseShellExecute = true,
                    Verb            = "runas",   // triggers the UAC elevation prompt
                });
                Close();
            }
            catch (Win32Exception)
            {
                // User declined the UAC prompt — leave the dialog open to retry.
            }
        }

        private void RunInstaller(bool repair)
        {
            string? product = NativeMethods.FindInstalledProductCode(UpgradeCode);
            if (product is null)
            {
                MessageBox.Show(this,
                    "Could not locate the installed RunAS Helper product to " +
                    (repair ? "repair" : "uninstall") + ".",
                    "RunAS Helper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // /fa = reinstall all files (re-runs service install + task creation);
            // /x  = uninstall. Both need elevation, so request it via the runas verb.
            string args = repair ? $"/fa {product} /qb!" : $"/x {product} /qb";
            try
            {
                Process.Start(new ProcessStartInfo("msiexec.exe", args)
                {
                    UseShellExecute = true,
                    Verb            = "runas",
                });
                if (repair)
                {
                    MessageBox.Show(this,
                        "Repair started. When it finishes, click Retry to re-check.",
                        "RunAS Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    // The product (including this app) is being removed — exit.
                    Application.Exit();
                }
            }
            catch (Win32Exception)
            {
                // UAC declined — no-op.
            }
        }

        // ── Small UI helpers ──────────────────────────────────────────────────

        private static Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button
            {
                Text      = text,
                AutoSize  = true,
                Padding   = new Padding(8, 2, 8, 2),
                Margin    = new Padding(6, 0, 0, 0),
                FlatStyle = FlatStyle.System,
                Visible   = false,
            };
            b.Click += onClick;
            return b;
        }

        private void SetButtons(bool retry, bool elevate, bool repair, bool uninstall, bool close)
        {
            _btnRetry.Visible     = retry;
            _btnElevate.Visible   = elevate;
            _btnRepair.Visible    = repair;
            _btnUninstall.Visible = uninstall;
            _btnClose.Visible     = close;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _client.LogMessage -= OnServiceLog;
            base.Dispose(disposing);
        }

        /// <summary>A single status row: glyph + bold title + detail line.</summary>
        private sealed class CheckRow
        {
            public const int RowHeight = 52;

            private readonly Label _glyph;
            private readonly Label _title;
            private readonly Label _detail;

            public CheckRow(string title)
            {
                _glyph = new Label
                {
                    Size      = new Size(26, 26),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font      = new Font("Segoe UI", 13f, FontStyle.Bold),
                    Text      = "•",
                    ForeColor = Color.Gray,
                };
                _title = new Label
                {
                    AutoSize = false,
                    Size     = new Size(420, 18),
                    Font     = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                    Text     = title,
                };
                _detail = new Label
                {
                    AutoSize  = false,
                    Size      = new Size(420, 18),
                    Font      = new Font("Segoe UI", 8.5f),
                    ForeColor = Color.DimGray,
                    Text      = "Pending",
                };
            }

            public void AddTo(Control parent, int y)
            {
                _glyph.Location  = new Point(0, y);
                _title.Location  = new Point(34, y);
                _detail.Location = new Point(34, y + 20);
                parent.Controls.Add(_glyph);
                parent.Controls.Add(_title);
                parent.Controls.Add(_detail);
            }

            public void SetRunning() => Set("…", Color.RoyalBlue, "Checking…", Color.DimGray);

            public void SetResult(bool ok, string detail) =>
                Set(ok ? "✓" : "✗",
                    ok ? Color.SeaGreen : Color.Firebrick,
                    detail,
                    ok ? Color.DimGray : Color.Firebrick);

            private void Set(string glyph, Color glyphColor, string detail, Color detailColor)
            {
                _glyph.Text       = glyph;
                _glyph.ForeColor  = glyphColor;
                _detail.Text      = detail;
                _detail.ForeColor = detailColor;
            }
        }
    }
}
