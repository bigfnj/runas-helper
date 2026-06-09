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

            lblPriority   = new Label();
            comboPriority = new ComboBox();
            lblPath       = new Label();
            comboPath     = new ComboBox();
            btnBrowse     = new Button();
            btnRun        = new Button();
            txtLog        = new TextBox();
            lblNotAdmin   = new Label();
            notifyIcon    = new NotifyIcon(components);
            trayMenu      = new ContextMenuStrip(components);
            menuShow      = new ToolStripMenuItem();
            menuSep       = new ToolStripSeparator();
            menuExit      = new ToolStripMenuItem();

            SuspendLayout();

            // lblPriority
            lblPriority.AutoSize = true;
            lblPriority.Location = new System.Drawing.Point(10, 14);
            lblPriority.Text     = "Priority:";

            // comboPriority
            comboPriority.DropDownStyle = ComboBoxStyle.DropDownList;
            comboPriority.Location      = new System.Drawing.Point(68, 10);
            comboPriority.Size          = new System.Drawing.Size(175, 23);
            comboPriority.Anchor        = AnchorStyles.Top | AnchorStyles.Left;
            comboPriority.Items.AddRange(new object[]
            {
                "Idle", "Below Normal", "Normal", "Above Normal", "High", "Realtime"
            });
            comboPriority.SelectedIndex = 2;

            // lblPath
            lblPath.AutoSize = true;
            lblPath.Location = new System.Drawing.Point(10, 47);
            lblPath.Text     = "Path:";

            // comboPath
            comboPath.Location = new System.Drawing.Point(10, 65);
            comboPath.Size     = new System.Drawing.Size(478, 23);
            comboPath.Anchor   = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            // btnBrowse
            btnBrowse.Location = new System.Drawing.Point(494, 65);
            btnBrowse.Size     = new System.Drawing.Size(38, 23);
            btnBrowse.Anchor   = AnchorStyles.Top | AnchorStyles.Right;
            btnBrowse.Text     = "...";

            // btnRun
            btnRun.Location  = new System.Drawing.Point(10, 102);
            btnRun.Size      = new System.Drawing.Size(522, 32);
            btnRun.Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            btnRun.Text      = "Launch Elevated";
            btnRun.FlatStyle = FlatStyle.System; // required for UAC shield

            // txtLog
            txtLog.Location    = new System.Drawing.Point(10, 148);
            txtLog.Size        = new System.Drawing.Size(522, 210);
            txtLog.Anchor      = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            txtLog.Multiline   = true;
            txtLog.ReadOnly    = true;
            txtLog.ScrollBars  = ScrollBars.Vertical;
            txtLog.BackColor   = System.Drawing.SystemColors.Window;
            txtLog.Font        = new System.Drawing.Font("Consolas", 8.5f);

            // lblNotAdmin
            lblNotAdmin.Location  = new System.Drawing.Point(10, 366);
            lblNotAdmin.Size      = new System.Drawing.Size(522, 20);
            lblNotAdmin.Anchor    = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            lblNotAdmin.ForeColor = System.Drawing.Color.Firebrick;
            lblNotAdmin.Text      = "⚠  Please exit and restart with \"Run As Administrator\"";
            lblNotAdmin.Visible   = false;

            // trayMenu
            trayMenu.Items.AddRange(new ToolStripItem[] { menuShow, menuSep, menuExit });

            menuShow.Text = "Show Window";
            menuShow.Font = new System.Drawing.Font(menuShow.Font, System.Drawing.FontStyle.Bold);

            menuExit.Text = "Exit";

            // notifyIcon
            notifyIcon.Text        = "RunAS Helper";
            notifyIcon.ContextMenuStrip = trayMenu;
            notifyIcon.Visible     = true;

            // Form
            ClientSize     = new System.Drawing.Size(542, 392);
            MinimumSize    = new System.Drawing.Size(440, 340);
            Text           = "RunAS Helper";
            StartPosition  = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new System.Drawing.SizeF(7f, 15f);
            AutoScaleMode       = AutoScaleMode.Font;

            Controls.Add(lblPriority);
            Controls.Add(comboPriority);
            Controls.Add(lblPath);
            Controls.Add(comboPath);
            Controls.Add(btnBrowse);
            Controls.Add(btnRun);
            Controls.Add(txtLog);
            Controls.Add(lblNotAdmin);

            ResumeLayout(false);
            PerformLayout();
        }

        // Controls
        private Label            lblPriority;
        private ComboBox         comboPriority;
        private Label            lblPath;
        private ComboBox         comboPath;
        private Button           btnBrowse;
        private Button           btnRun;
        private TextBox          txtLog;
        private Label            lblNotAdmin;
        private NotifyIcon       notifyIcon;
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem menuShow;
        private ToolStripSeparator menuSep;
        private ToolStripMenuItem menuExit;
    }
}
