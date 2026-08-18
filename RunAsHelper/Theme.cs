using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using RunAsHelper.Core;

namespace RunAsHelper
{
    /// <summary>Which palette the app uses. <see cref="System"/> follows Windows.</summary>
    internal enum ThemeMode
    {
        System = 0,
        Light  = 1,
        Dark   = 2,
    }

    /// <summary>
    /// Applies a light or dark palette across the app's forms.
    ///
    /// WinForms has no built-in dark mode, so this walks the control tree and sets colours
    /// explicitly. Two things cannot be done with managed properties alone and need the
    /// window manager: the title bar (<c>DwmSetWindowAttribute</c>) and the native parts of
    /// a ListView — its column headers and scrollbars — which only follow along after
    /// <c>SetWindowTheme(..., "DarkMode_Explorer")</c>.
    /// </summary>
    internal static class Theme
    {
        // Windows publishes the user's app-theme preference here; 0 = dark.
        private const string PersonalizeKey =
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        public static ThemeMode Mode { get; set; } = ThemeMode.System;

        /// <summary>Whether the dark palette applies right now, resolving <see cref="ThemeMode.System"/>.</summary>
        public static bool IsDark => Mode switch
        {
            ThemeMode.Dark  => true,
            ThemeMode.Light => false,
            _               => SystemPrefersDark(),
        };

        public static bool SystemPrefersDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch { return false; }   // no preference readable → treat as light
        }

        // ── Palette ──────────────────────────────────────────────────────────

        public static Color Back      => IsDark ? Color.FromArgb(32, 32, 32)    : SystemColors.Control;
        public static Color Fore      => IsDark ? Color.FromArgb(240, 240, 240) : SystemColors.ControlText;
        public static Color FieldBack => IsDark ? Color.FromArgb(24, 24, 24)    : SystemColors.Window;
        public static Color FieldFore => IsDark ? Color.FromArgb(240, 240, 240) : SystemColors.WindowText;
        public static Color Accent    => IsDark ? Color.FromArgb(64, 64, 64)    : SystemColors.ControlLight;

        /// <summary>A warning/attention colour that stays legible on either background.</summary>
        public static Color Warn => IsDark ? Color.FromArgb(255, 170, 90) : Color.FromArgb(150, 60, 0);

        /// <summary>An error colour that stays legible on either background.</summary>
        public static Color Danger => IsDark ? Color.FromArgb(255, 120, 120) : Color.FromArgb(170, 0, 0);

        // ── Application ──────────────────────────────────────────────────────

        /// <summary>Applies the current palette to a form and everything inside it.</summary>
        public static void Apply(Form form)
        {
            if (form is null) return;
            bool dark = IsDark;

            form.BackColor = Back;
            form.ForeColor = Fore;
            ApplyTitleBar(form, dark);

            foreach (Control child in form.Controls)
                ApplyTo(child, dark);
        }

        /// <summary>
        /// Themes a ToolStrip that is not part of a form's control tree — the tray's
        /// context menu, which is owned by the NotifyIcon rather than the window.
        /// </summary>
        public static void Apply2(ToolStrip? strip)
        {
            if (strip is null) return;
            strip.BackColor = Back;
            strip.ForeColor = Fore;
            strip.Renderer  = new ToolStripProfessionalRenderer(new ThemeColorTable(IsDark));
        }

        private static void ApplyTo(Control control, bool dark)
        {
            switch (control)
            {
                case TextBox tb:
                    tb.BackColor = FieldBack;
                    tb.ForeColor = FieldFore;
                    tb.BorderStyle = dark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
                    break;

                case ListView lv:
                    lv.BackColor = FieldBack;
                    lv.ForeColor = FieldFore;
                    // Headers and scrollbars are drawn by the OS, not by us.
                    if (lv.IsHandleCreated)
                        NativeMethods.SetWindowTheme(lv.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
                    break;

                case ComboBox cb:
                    cb.BackColor = FieldBack;
                    cb.ForeColor = FieldFore;
                    cb.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                    break;

                case Button btn:
                    btn.BackColor = Accent;
                    btn.ForeColor = Fore;
                    // FlatStyle.System ignores BackColor entirely, so dark needs Flat.
                    btn.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.System;
                    btn.FlatAppearance.BorderColor = dark ? Color.FromArgb(90, 90, 90) : Color.Gray;
                    break;

                case NumericUpDown nud:
                    nud.BackColor = FieldBack;
                    nud.ForeColor = FieldFore;
                    break;

                case MenuStrip menu:
                    menu.BackColor = Back;
                    menu.ForeColor = Fore;
                    menu.Renderer  = new ToolStripProfessionalRenderer(new ThemeColorTable(dark));
                    break;

                case StatusStrip status:
                    status.BackColor = Back;
                    status.ForeColor = Fore;
                    status.Renderer  = new ToolStripProfessionalRenderer(new ThemeColorTable(dark));
                    break;

                case ContextMenuStrip ctx:
                    ctx.BackColor = Back;
                    ctx.ForeColor = Fore;
                    ctx.Renderer  = new ToolStripProfessionalRenderer(new ThemeColorTable(dark));
                    break;

                default:
                    control.BackColor = Back;
                    control.ForeColor = Fore;
                    break;
            }

            foreach (Control child in control.Controls)
                ApplyTo(child, dark);
        }

        /// <summary>Paints the non-client title bar to match. No-op on builds that predate it.</summary>
        public static void ApplyTitleBar(Form form, bool dark)
        {
            if (form is null || !form.IsHandleCreated) return;
            int on = dark ? 1 : 0;
            try
            {
                if (NativeMethods.DwmSetWindowAttribute(
                        form.Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                {
                    NativeMethods.DwmSetWindowAttribute(
                        form.Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1, ref on, sizeof(int));
                }
            }
            catch { /* theming is cosmetic — never let it break a window */ }
        }

        /// <summary>Colours for menu and status strips, which ignore plain BackColor.</summary>
        private sealed class ThemeColorTable(bool dark) : ProfessionalColorTable
        {
            private readonly Color _back   = dark ? Color.FromArgb(32, 32, 32) : SystemColors.Control;
            private readonly Color _hover  = dark ? Color.FromArgb(60, 60, 60) : SystemColors.ControlLight;
            private readonly Color _border = dark ? Color.FromArgb(80, 80, 80) : SystemColors.ControlDark;

            public override Color MenuStripGradientBegin        => _back;
            public override Color MenuStripGradientEnd          => _back;
            public override Color StatusStripGradientBegin      => _back;
            public override Color StatusStripGradientEnd        => _back;
            public override Color ToolStripDropDownBackground   => _back;
            public override Color ImageMarginGradientBegin      => _back;
            public override Color ImageMarginGradientMiddle     => _back;
            public override Color ImageMarginGradientEnd        => _back;
            public override Color MenuItemSelected              => _hover;
            public override Color MenuItemSelectedGradientBegin => _hover;
            public override Color MenuItemSelectedGradientEnd   => _hover;
            public override Color MenuItemPressedGradientBegin  => _back;
            public override Color MenuItemPressedGradientEnd    => _back;
            public override Color MenuBorder                    => _border;
            public override Color MenuItemBorder                => _border;
            public override Color SeparatorDark                 => _border;
            public override Color SeparatorLight                => _border;
        }
    }
}
