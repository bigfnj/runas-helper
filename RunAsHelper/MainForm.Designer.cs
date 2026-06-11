using System.Windows.Forms;

namespace RunAsHelper
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();

            // ── Menu strip ───────────────────────────────────────────────────
            menuStrip         = new MenuStrip();
            menuTools         = new ToolStripMenuItem();
            menuSettings      = new ToolStripMenuItem();
            menuToolsSep1     = new ToolStripSeparator();
            menuValidate      = new ToolStripMenuItem();
            menuToolsSepV     = new ToolStripSeparator();
            menuImport        = new ToolStripMenuItem();
            menuExport        = new ToolStripMenuItem();
            menuToolsSep2     = new ToolStripSeparator();
            menuClearRecent   = new ToolStripMenuItem();
            menuToolsOpenPwsh = new ToolStripMenuItem();
            menuToolsSepP     = new ToolStripSeparator();

            // ── Form controls ────────────────────────────────────────────────
            lblPriority    = new Label();
            comboPriority  = new ComboBox();
            lblPath        = new Label();
            comboPath      = new ComboBox();
            btnSave        = new Button();
            btnBrowse      = new Button();
            btnRun         = new Button();
            txtLog         = new TextBox();
            lblNotAdmin    = new Label();

            // ── Tray ─────────────────────────────────────────────────────────
            notifyIcon        = new NotifyIcon(components);
            trayMenu          = new ContextMenuStrip(components);
            menuSavedApps     = new ToolStripMenuItem();
            menuSavedSep      = new ToolStripSeparator();
            menuRecent        = new ToolStripMenuItem();
            menuOpenPwsh      = new ToolStripMenuItem();
            menuLaunchSep     = new ToolStripSeparator();
            menuStartService  = new ToolStripMenuItem();
            menuShow          = new ToolStripMenuItem();
            menuSep           = new ToolStripSeparator();
            menuExit          = new ToolStripMenuItem();

            SuspendLayout();

            // ── Tools menu ───────────────────────────────────────────────────
            menuSettings.Text    = "Settings...";
            menuValidate.Text    = "Validate Installation...";
            menuImport.Text      = "Import Saved Apps...";
            menuExport.Text      = "Export Saved Apps...";
            menuClearRecent.Text = "Clear Recent History";
            menuToolsOpenPwsh.Text = "Open PowerShell (TrustedInstaller)";

            menuTools.Text = "Tools";
            menuTools.DropDownItems.AddRange(new ToolStripItem[]
            {
                menuSettings, menuToolsSep1,
                menuValidate, menuToolsSepV,
                menuToolsOpenPwsh, menuToolsSepP,
                menuImport, menuExport,
                menuToolsSep2, menuClearRecent,
            });

            menuStrip.Items.Add(menuTools);
            menuStrip.Dock = DockStyle.Top;

            // ── Priority ─────────────────────────────────────────────────────
            lblPriority.AutoSize = true;
            lblPriority.Location = new System.Drawing.Point(10, 38);
            lblPriority.Text     = "Priority:";

            comboPriority.DropDownStyle = ComboBoxStyle.DropDownList;
            comboPriority.Location      = new System.Drawing.Point(68, 34);
            comboPriority.Size          = new System.Drawing.Size(175, 23);
            comboPriority.Anchor        = AnchorStyles.Top | AnchorStyles.Left;
            comboPriority.Items.AddRange(new object[]
            {
                "Idle", "Below Normal", "Normal", "Above Normal", "High", "Realtime"
            });
            comboPriority.SelectedIndex = 2;

            // ── Path row ─────────────────────────────────────────────────────
            lblPath.AutoSize = true;
            lblPath.Location = new System.Drawing.Point(10, 71);
            lblPath.Text     = "Path:";

            comboPath.Location = new System.Drawing.Point(10, 89);
            comboPath.Size     = new System.Drawing.Size(438, 23);
            comboPath.Anchor   = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            btnSave.Location  = new System.Drawing.Point(452, 89);
            btnSave.Size      = new System.Drawing.Size(38, 23);
            btnSave.Anchor    = AnchorStyles.Top | AnchorStyles.Right;
            btnSave.Text      = "★";
            btnSave.FlatStyle = FlatStyle.System;

            btnBrowse.Location = new System.Drawing.Point(494, 89);
            btnBrowse.Size     = new System.Drawing.Size(38, 23);
            btnBrowse.Anchor   = AnchorStyles.Top | AnchorStyles.Right;
            btnBrowse.Text     = "...";

            // ── Run button ───────────────────────────────────────────────────
            btnRun.Location  = new System.Drawing.Point(10, 126);
            btnRun.Size      = new System.Drawing.Size(522, 32);
            btnRun.Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            btnRun.Text      = "Launch Elevated";
            btnRun.FlatStyle = FlatStyle.System;

            // ── Log ──────────────────────────────────────────────────────────
            txtLog.Location   = new System.Drawing.Point(10, 172);
            txtLog.Size       = new System.Drawing.Size(522, 210);
            txtLog.Anchor     = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            txtLog.Multiline  = true;
            txtLog.ReadOnly   = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BackColor  = System.Drawing.SystemColors.Window;
            txtLog.Font       = new System.Drawing.Font("Consolas", 8.5f);

            // ── Status label ─────────────────────────────────────────────────
            lblNotAdmin.Location  = new System.Drawing.Point(10, 390);
            lblNotAdmin.Size      = new System.Drawing.Size(522, 20);
            lblNotAdmin.Anchor    = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            lblNotAdmin.ForeColor = System.Drawing.Color.Firebrick;
            lblNotAdmin.Text      = "⚠  Please exit and restart with \"Run As Administrator\"";
            lblNotAdmin.Visible   = false;

            // ── Tray menu ────────────────────────────────────────────────────
            menuSavedApps.Text    = "Saved Applications";
            menuSavedApps.Enabled = false;

            menuRecent.Text    = "Recent";
            menuRecent.Enabled = false;

            menuStartService.Text    = "Start Service";
            menuStartService.Visible = false;

            menuShow.Text = "Show Window";
            menuShow.Font = new System.Drawing.Font(menuShow.Font, System.Drawing.FontStyle.Bold);

            menuExit.Text = "Exit";

            trayMenu.Items.AddRange(new ToolStripItem[]
            {
                menuSavedApps, menuSavedSep, menuRecent,
                menuOpenPwsh, menuLaunchSep, menuStartService,
                menuShow, menuSep, menuExit,
            });

            notifyIcon.Text             = "RunAS Helper";
            notifyIcon.ContextMenuStrip = trayMenu;
            notifyIcon.Visible          = true;

            // ── Form ─────────────────────────────────────────────────────────
            ClientSize          = new System.Drawing.Size(542, 416);
            MinimumSize         = new System.Drawing.Size(440, 364);
            Text                = "RunAS Helper";
            StartPosition       = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new System.Drawing.SizeF(7f, 15f);
            AutoScaleMode       = AutoScaleMode.Font;
            MainMenuStrip       = menuStrip;

            Controls.Add(menuStrip);
            Controls.Add(lblPriority);
            Controls.Add(comboPriority);
            Controls.Add(lblPath);
            Controls.Add(comboPath);
            Controls.Add(btnSave);
            Controls.Add(btnBrowse);
            Controls.Add(btnRun);
            Controls.Add(txtLog);
            Controls.Add(lblNotAdmin);

            ResumeLayout(false);
            PerformLayout();
        }

        // ── Menu strip ───────────────────────────────────────────────────────
        private MenuStrip            menuStrip;
        private ToolStripMenuItem    menuTools;
        private ToolStripMenuItem    menuSettings;
        private ToolStripSeparator   menuToolsSep1;
        private ToolStripMenuItem    menuValidate;
        private ToolStripSeparator   menuToolsSepV;
        private ToolStripMenuItem    menuImport;
        private ToolStripMenuItem    menuExport;
        private ToolStripSeparator   menuToolsSep2;
        private ToolStripMenuItem    menuClearRecent;
        private ToolStripMenuItem    menuToolsOpenPwsh;
        private ToolStripSeparator   menuToolsSepP;

        // ── Form controls ────────────────────────────────────────────────────
        private Label              lblPriority;
        private ComboBox           comboPriority;
        private Label              lblPath;
        private ComboBox           comboPath;
        private Button             btnSave;
        private Button             btnBrowse;
        private Button             btnRun;
        private TextBox            txtLog;
        private Label              lblNotAdmin;

        // ── Tray ─────────────────────────────────────────────────────────────
        private NotifyIcon         notifyIcon;
        private ContextMenuStrip   trayMenu;
        private ToolStripMenuItem  menuSavedApps;
        private ToolStripSeparator menuSavedSep;
        private ToolStripMenuItem  menuRecent;
        private ToolStripMenuItem  menuOpenPwsh;
        private ToolStripSeparator menuLaunchSep;
        private ToolStripMenuItem  menuStartService;
        private ToolStripMenuItem  menuShow;
        private ToolStripSeparator menuSep;
        private ToolStripMenuItem  menuExit;
    }
}
