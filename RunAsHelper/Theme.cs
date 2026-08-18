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
    /// Light/dark theming for the app's windows.
    ///
    /// The palette and control rules deliberately mirror the sibling desktopPet project
    /// (#202020 background, #2D2D30 surface, #F0F0F0 text, #46464A border) so the two tools
    /// look like they belong together.
    ///
    /// WinForms has no dark mode, so this walks the control tree and sets colours by hand.
    /// Three things cannot be done with managed properties alone:
    ///   • the title bar, via <c>DwmSetWindowAttribute</c>;
    ///   • native control chrome (edit borders, combo dropdowns, list scrollbars and
    ///     selection), via <c>SetWindowTheme</c> with "DarkMode_CFD"/"DarkMode_Explorer";
    ///   • ListView column headers, which comctl32 keeps painting light regardless — those
    ///     need owner-draw (see <see cref="ApplyListView"/>).
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

        // ── Palette (shared with desktopPet) ─────────────────────────────────

        private static readonly Color DarkBg      = Color.FromArgb(32, 32, 32);
        private static readonly Color DarkSurface = Color.FromArgb(45, 45, 48);
        private static readonly Color DarkText    = Color.FromArgb(240, 240, 240);
        private static readonly Color DarkMuted   = Color.FromArgb(170, 170, 170);
        private static readonly Color DarkBorder  = Color.FromArgb(70, 70, 74);
        private static readonly Color DarkHeader  = Color.FromArgb(58, 58, 61);

        public static Color Back      => IsDark ? DarkBg      : SystemColors.Control;
        public static Color Fore      => IsDark ? DarkText    : SystemColors.ControlText;
        public static Color FieldBack => IsDark ? DarkSurface : SystemColors.Window;
        public static Color FieldFore => IsDark ? DarkText    : SystemColors.WindowText;
        public static Color Border    => IsDark ? DarkBorder  : SystemColors.ControlDark;
        public static Color Muted     => IsDark ? DarkMuted   : SystemColors.GrayText;

        /// <summary>A warning/attention colour that stays legible on either background.</summary>
        public static Color Warn => IsDark ? Color.FromArgb(255, 176, 96) : Color.FromArgb(150, 60, 0);

        /// <summary>An error colour that stays legible on either background.</summary>
        public static Color Danger => IsDark ? Color.FromArgb(255, 128, 128) : Color.FromArgb(170, 0, 0);

        // ── Application ──────────────────────────────────────────────────────

        /// <summary>Applies the current palette to a form and everything inside it.</summary>
        public static void Apply(Form form)
        {
            if (form is null || form.IsDisposed) return;
            bool dark = IsDark;

            ApplyTitleBar(form, dark);
            form.BackColor = Back;
            form.ForeColor = Fore;

            foreach (Control child in form.Controls)
                ApplyTo(child, dark);
        }

        /// <summary>
        /// Themes a ToolStrip that is not in a form's control tree — the tray's context
        /// menu, which belongs to the NotifyIcon rather than to any window.
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
                case Label label:
                    // Transparent so the parent's colour shows through rather than a patch.
                    label.BackColor = Color.Transparent;
                    label.ForeColor = ReadableLabelColor(label.ForeColor, dark);
                    break;

                case ListView lv:
                    ApplyListView(lv, dark);
                    break;

                case TextBox tb:
                    tb.BackColor = FieldBack;
                    tb.ForeColor = FieldFore;
                    DarkenNative(tb, dark, "DarkMode_CFD");   // dark edit border
                    break;

                case ComboBox cb:
                    cb.BackColor = FieldBack;
                    cb.ForeColor = FieldFore;
                    cb.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                    DarkenNative(cb, dark, "DarkMode_CFD");   // dark dropdown
                    break;

                case NumericUpDown nud:
                    // The outer control ignores BackColor for its inner edit and spin
                    // buttons, which would otherwise stay white boxes on a dark form.
                    nud.BackColor = FieldBack;
                    nud.ForeColor = FieldFore;
                    foreach (Control inner in nud.Controls)
                    {
                        inner.BackColor = FieldBack;
                        inner.ForeColor = FieldFore;
                    }
                    break;

                case CheckBox or RadioButton:
                    control.BackColor = Color.Transparent;
                    control.ForeColor = Fore;
                    break;

                case Button btn:
                    btn.BackColor = dark ? DarkSurface : SystemColors.Control;
                    btn.ForeColor = Fore;
                    // FlatStyle.System ignores BackColor entirely, so dark needs Flat.
                    btn.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.System;
                    btn.FlatAppearance.BorderColor = Border;
                    btn.UseVisualStyleBackColor    = !dark;
                    break;

                case MenuStrip or StatusStrip or ContextMenuStrip:
                    var strip = (ToolStrip)control;
                    strip.BackColor = Back;
                    strip.ForeColor = Fore;
                    strip.Renderer  = new ToolStripProfessionalRenderer(new ThemeColorTable(dark));
                    break;

                default:
                    control.BackColor = Back;
                    control.ForeColor = Fore;
                    break;
            }

            foreach (Control child in control.Controls)
                ApplyTo(child, dark);
        }

        /// <summary>
        /// ListView needs more than colours. Its grid lines are drawn by comctl32 in a fixed
        /// light colour with no way to change them (they read as harsh white on dark), and
        /// its column headers keep their light background even under DarkMode_Explorer — so
        /// dark mode turns grid lines off and owner-draws the headers.
        /// </summary>
        private static void ApplyListView(ListView lv, bool dark)
        {
            lv.BackColor = FieldBack;
            lv.ForeColor = FieldFore;
            lv.GridLines = !dark;
            DarkenNative(lv, dark, "DarkMode_Explorer");   // dark scrollbars + selection

            // Re-hooking on every apply would stack handlers, so detach first.
            lv.DrawColumnHeader -= OnDrawColumnHeader;
            lv.DrawItem         -= OnDrawDefault;
            lv.DrawSubItem      -= OnDrawSubItemDefault;

            if (dark)
            {
                lv.OwnerDraw = true;
                lv.DrawColumnHeader += OnDrawColumnHeader;
                // Rows and cells stay OS-drawn; only the headers are ours. Without these
                // two handlers an owner-drawn Details view renders nothing at all.
                lv.DrawItem    += OnDrawDefault;
                lv.DrawSubItem += OnDrawSubItemDefault;
            }
            else
            {
                lv.OwnerDraw = false;
            }
            lv.Invalidate();
        }

        private static void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
        {
            using var back = new SolidBrush(DarkHeader);
            e.Graphics.FillRectangle(back, e.Bounds);
            using var edge = new Pen(DarkBorder);
            e.Graphics.DrawLine(edge, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom - 1);
            e.Graphics.DrawLine(edge, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right - 1, e.Bounds.Bottom - 1);

            var text = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty,
                e.Font ?? SystemFonts.DefaultFont, text, DarkText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
        }

        // Status colours (pass/fail/warn) carry meaning, so they survive theming — but the
        // "dim grey" hint colours are unreadable on a dark background and get lifted to the
        // muted tone instead. Anything else follows the normal foreground.
        private static Color ReadableLabelColor(Color current, bool dark)
        {
            if (!dark) return current == DarkText || current == DarkMuted ? SystemColors.ControlText : current;

            if (current == Color.DimGray || current == Color.Gray || current == SystemColors.GrayText)
                return DarkMuted;
            if (current == Color.Firebrick) return Danger;
            if (current == Color.SeaGreen)  return Color.FromArgb(110, 200, 130);
            return DarkText;
        }

        private static void OnDrawDefault(object? sender, DrawListViewItemEventArgs e)
            => e.DrawDefault = true;

        private static void OnDrawSubItemDefault(object? sender, DrawListViewSubItemEventArgs e)
            => e.DrawDefault = true;

        // Tells comctl32 to use the OS dark visual style for a control's native chrome.
        private static void DarkenNative(Control c, bool dark, string appName)
        {
            // A form is usually themed before its children have handles, and SetWindowTheme
            // silently does nothing without one — so defer until the handle exists.
            if (!c.IsHandleCreated)
            {
                void OnCreated(object? s, EventArgs e)
                {
                    c.HandleCreated -= OnCreated;
                    DarkenNative(c, dark, appName);
                }
                c.HandleCreated += OnCreated;
                return;
            }
            try { NativeMethods.SetWindowTheme(c.Handle, dark ? appName : "Explorer", null); }
            catch { /* cosmetic only */ }
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
            private readonly Color _back   = dark ? DarkBg      : SystemColors.Control;
            private readonly Color _hover  = dark ? Color.FromArgb(62, 62, 66) : SystemColors.ControlLight;
            private readonly Color _border = dark ? DarkBorder  : SystemColors.ControlDark;

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
