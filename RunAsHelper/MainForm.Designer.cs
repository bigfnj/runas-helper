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
            menuToolsOpenPwsh = new ToolStripMenuItem();
            menuToolsSepP     = new ToolStripSeparator();
            menuImport        = new ToolStripMenuItem();
            menuExport        = new ToolStripMenuItem();
            menuToolsSep2     = new ToolStripSeparator();
            menuClearRecent   = new ToolStripMenuItem();
            menuToolsSepH     = new ToolStripSeparator();
            menuHowToUse      = new ToolStripMenuItem();

            // ── Top panel (quick run + saved-apps toolbar) ────────────────────
            panelTop      = new Panel();
            lblQuick      = new Label();
            comboPriority = new ComboBox();
            comboPath     = new ComboBox();
            btnBrowse     = new Button();
            btnRunTI      = new Button();
            btnRunSystem  = new Button();
            btnActivate   = new Button();
            lblSaved      = new Label();
            btnAddApp     = new Button();
            btnRunSaved   = new Button();
            btnEditApp    = new Button();
            btnRemoveApp  = new Button();
            btnUpApp      = new Button();
            btnDownApp    = new Button();

            // ── Saved-apps list (fills) ───────────────────────────────────────
            lvApps = new ListView();

            // ── Bottom panel (log + status) ───────────────────────────────────
            panelBottom = new Panel();
            txtLog      = new TextBox();
            lblNotAdmin = new Label();

            // ── Tray ─────────────────────────────────────────────────────────
            notifyIcon       = new NotifyIcon(components);
            trayMenu         = new ContextMenuStrip(components);
            menuActivate     = new ToolStripMenuItem();
            menuSavedApps    = new ToolStripMenuItem();
            menuSavedSep     = new ToolStripSeparator();
            menuRecent       = new ToolStripMenuItem();
            menuOpenPwsh     = new ToolStripMenuItem();
            menuLaunchSep    = new ToolStripSeparator();
            menuStartService = new ToolStripMenuItem();
            menuShow         = new ToolStripMenuItem();
            menuSep          = new ToolStripSeparator();
            menuExit         = new ToolStripMenuItem();

            SuspendLayout();

            // ── Tools menu ───────────────────────────────────────────────────
            menuSettings.Text      = "Settings...";
            menuValidate.Text      = "Validate Installation...";
            menuToolsOpenPwsh.Text = "Open PowerShell (TrustedInstaller)";
            menuImport.Text        = "Import Saved Apps...";
            menuExport.Text        = "Export Saved Apps...";
            menuClearRecent.Text   = "Clear Recent History";
            menuHowToUse.Text      = "How to Use...";

            menuTools.Text = "Tools";
            menuTools.DropDownItems.AddRange(new ToolStripItem[]
            {
                menuSettings, menuToolsSep1,
                menuValidate, menuToolsSepV,
                menuToolsOpenPwsh, menuToolsSepP,
                menuImport, menuExport,
                menuToolsSep2, menuClearRecent,
                menuToolsSepH, menuHowToUse,
            });

            menuStrip.Items.Add(menuTools);
            menuStrip.Dock = DockStyle.Top;

            // ── Top panel ─────────────────────────────────────────────────────
            panelTop.Dock   = DockStyle.Top;
            panelTop.Height  = 144;

            lblQuick.AutoSize = true;
            lblQuick.Location = new System.Drawing.Point(8, 6);
            lblQuick.Text     = "Quick run (one-off):";

            // Row 1: priority + browse + path. Browse sits at a fixed position right
            // after the priority dropdown (left-anchored, so it is always visible);
            // the path box is the only control that stretches to the right edge.
            comboPriority.DropDownStyle = ComboBoxStyle.DropDownList;
            comboPriority.Location      = new System.Drawing.Point(8, 26);
            comboPriority.Size          = new System.Drawing.Size(96, 23);
            comboPriority.Anchor        = AnchorStyles.Top | AnchorStyles.Left;
            comboPriority.Items.AddRange(new object[]
            { "Idle", "Below Normal", "Normal", "Above Normal", "High", "Realtime" });
            comboPriority.SelectedIndex = 2;

            btnBrowse.Location = new System.Drawing.Point(110, 25);
            btnBrowse.Size     = new System.Drawing.Size(80, 25);
            btnBrowse.Anchor   = AnchorStyles.Top | AnchorStyles.Left;
            btnBrowse.Text     = "Browse...";

            // Width leaves a ~24px right margin at the design width; the Left|Right
            // anchor keeps that margin constant as the window grows (the box stretches
            // but never hugs the border).
            comboPath.Location = new System.Drawing.Point(196, 26);
            comboPath.Size     = new System.Drawing.Size(424, 23);
            comboPath.Anchor   = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            // Row 2: one explicit run button per account — the account is chosen by
            // which button you click, so there is no separate account dropdown. Both
            // carry the UAC shield (BCM_SETSHIELD, applied once elevated in
            // MainForm.OnLoad / ApplyServiceState).
            btnRunTI.Location  = new System.Drawing.Point(8, 55);
            btnRunTI.Size      = new System.Drawing.Size(190, 26);
            btnRunTI.Anchor    = AnchorStyles.Top | AnchorStyles.Left;
            btnRunTI.Text      = "Run as TrustedInstaller";
            btnRunTI.FlatStyle = FlatStyle.System;

            btnRunSystem.Location  = new System.Drawing.Point(206, 55);
            btnRunSystem.Size      = new System.Drawing.Size(130, 26);
            btnRunSystem.Anchor    = AnchorStyles.Top | AnchorStyles.Left;
            btnRunSystem.Text      = "Run as SYSTEM";
            btnRunSystem.FlatStyle = FlatStyle.System;

            lblSaved.AutoSize = true;
            lblSaved.Location = new System.Drawing.Point(8, 88);
            lblSaved.Font     = new System.Drawing.Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold);
            lblSaved.Text     = "Saved applications";

            btnAddApp.Location  = new System.Drawing.Point(8, 110);
            btnAddApp.Size      = new System.Drawing.Size(130, 26);
            btnAddApp.Anchor    = AnchorStyles.Top | AnchorStyles.Left;
            btnAddApp.Text      = "Add Application";
            btnAddApp.FlatStyle = FlatStyle.System;

            btnRunSaved.Location = new System.Drawing.Point(146, 110);
            btnRunSaved.Size     = new System.Drawing.Size(64, 26);
            btnRunSaved.Anchor   = AnchorStyles.Top | AnchorStyles.Left;
            btnRunSaved.Text     = "Run";

            btnEditApp.Location = new System.Drawing.Point(214, 110);
            btnEditApp.Size     = new System.Drawing.Size(64, 26);
            btnEditApp.Anchor   = AnchorStyles.Top | AnchorStyles.Left;
            btnEditApp.Text     = "Edit";

            btnRemoveApp.Location = new System.Drawing.Point(282, 110);
            btnRemoveApp.Size     = new System.Drawing.Size(76, 26);
            btnRemoveApp.Anchor   = AnchorStyles.Top | AnchorStyles.Left;
            btnRemoveApp.Text     = "Remove";

            btnUpApp.Location = new System.Drawing.Point(364, 110);
            btnUpApp.Size     = new System.Drawing.Size(30, 26);
            btnUpApp.Anchor   = AnchorStyles.Top | AnchorStyles.Left;
            btnUpApp.Text     = "↑";

            btnDownApp.Location = new System.Drawing.Point(398, 110);
            btnDownApp.Size     = new System.Drawing.Size(30, 26);
            btnDownApp.Anchor   = AnchorStyles.Top | AnchorStyles.Left;
            btnDownApp.Text     = "↓";

            panelTop.Controls.AddRange(new Control[]
            {
                lblQuick, comboPriority, comboPath, btnBrowse, btnRunTI, btnRunSystem,
                lblSaved, btnAddApp, btnRunSaved, btnEditApp, btnRemoveApp, btnUpApp, btnDownApp,
            });

            // ── Saved-apps list ───────────────────────────────────────────────
            lvApps.Dock          = DockStyle.Fill;
            lvApps.View          = View.Details;
            lvApps.FullRowSelect = true;
            lvApps.GridLines     = true;
            lvApps.MultiSelect   = false;
            lvApps.HideSelection = false;
            lvApps.Columns.Add("Name", 160);
            lvApps.Columns.Add("File Location", 330);
            lvApps.Columns.Add("Parameter", 150);

            // ── Bottom panel ──────────────────────────────────────────────────
            panelBottom.Dock   = DockStyle.Bottom;
            panelBottom.Height  = 116;

            // Activate bar: a prominent strip at the top of the bottom panel,
            // shown only when NOT elevated; it disappears once the elevated
            // instance takes over (invisible Dock=Top controls take no space).
            btnActivate.Dock      = DockStyle.Top;
            btnActivate.Height    = 30;
            btnActivate.Text      = "Activate — elevate with Avecto";
            btnActivate.FlatStyle = FlatStyle.System;
            btnActivate.Visible   = false;

            txtLog.Dock       = DockStyle.Fill;
            txtLog.Multiline  = true;
            txtLog.ReadOnly   = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BackColor  = System.Drawing.SystemColors.Window;
            txtLog.Font       = new System.Drawing.Font("Consolas", 8.5f);

            lblNotAdmin.Dock      = DockStyle.Bottom;
            lblNotAdmin.Height    = 20;
            lblNotAdmin.ForeColor = System.Drawing.Color.Firebrick;
            lblNotAdmin.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            lblNotAdmin.Padding   = new Padding(4, 0, 0, 0);
            lblNotAdmin.Text      = "";
            lblNotAdmin.Visible   = false;

            panelBottom.Controls.Add(txtLog);
            panelBottom.Controls.Add(lblNotAdmin);
            panelBottom.Controls.Add(btnActivate);

            // ── Tray menu ────────────────────────────────────────────────────
            menuActivate.Text    = "Activate — elevate with Avecto";
            menuActivate.Visible = false;
            menuActivate.Font    = new System.Drawing.Font(menuActivate.Font, System.Drawing.FontStyle.Bold);

            menuSavedApps.Text    = "Saved Applications";
            menuSavedApps.Enabled = false;

            menuRecent.Text    = "Recent";
            menuRecent.Enabled = false;

            menuOpenPwsh.Text = "Open PowerShell (TrustedInstaller)";

            menuStartService.Text    = "Start Service";
            menuStartService.Visible = false;

            menuShow.Text = "Show Window";
            menuShow.Font = new System.Drawing.Font(menuShow.Font, System.Drawing.FontStyle.Bold);

            menuExit.Text = "Exit";

            trayMenu.Items.AddRange(new ToolStripItem[]
            {
                menuActivate,
                menuSavedApps, menuSavedSep, menuRecent,
                menuOpenPwsh, menuLaunchSep, menuStartService,
                menuShow, menuSep, menuExit,
            });

            notifyIcon.Text             = "RunAS Helper";
            notifyIcon.ContextMenuStrip = trayMenu;
            notifyIcon.Visible          = true;

            // ── Form ─────────────────────────────────────────────────────────
            ClientSize          = new System.Drawing.Size(644, 520);
            MinimumSize         = new System.Drawing.Size(520, 420);
            Text                = "RunAS Helper";
            StartPosition       = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new System.Drawing.SizeF(7f, 15f);
            AutoScaleMode       = AutoScaleMode.Font;
            MainMenuStrip       = menuStrip;

            // Add order matters for docking: Fill first, then edges, outermost last.
            Controls.Add(lvApps);
            Controls.Add(panelBottom);
            Controls.Add(panelTop);
            Controls.Add(menuStrip);

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
        private ToolStripMenuItem    menuToolsOpenPwsh;
        private ToolStripSeparator   menuToolsSepP;
        private ToolStripMenuItem    menuImport;
        private ToolStripMenuItem    menuExport;
        private ToolStripSeparator   menuToolsSep2;
        private ToolStripMenuItem    menuClearRecent;
        private ToolStripSeparator   menuToolsSepH;
        private ToolStripMenuItem    menuHowToUse;

        // ── Top panel ──────────────────────────────────────────────────────────
        private Panel    panelTop;
        private Label    lblQuick;
        private ComboBox comboPriority;
        private ComboBox comboPath;
        private Button   btnBrowse;
        private Button   btnRunTI;
        private Button   btnRunSystem;
        private Button   btnActivate;
        private Label    lblSaved;
        private Button   btnAddApp;
        private Button   btnRunSaved;
        private Button   btnEditApp;
        private Button   btnRemoveApp;
        private Button   btnUpApp;
        private Button   btnDownApp;

        // ── Saved-apps list ─────────────────────────────────────────────────────
        private ListView lvApps;

        // ── Bottom panel ─────────────────────────────────────────────────────────
        private Panel   panelBottom;
        private TextBox txtLog;
        private Label   lblNotAdmin;

        // ── Tray ─────────────────────────────────────────────────────────────────
        private NotifyIcon         notifyIcon;
        private ContextMenuStrip   trayMenu;
        private ToolStripMenuItem  menuActivate;
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
