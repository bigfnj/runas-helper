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
    /// The small amount of theming WinForms does not do for us.
    ///
    /// <c>Application.SetColorMode</c>, called once at startup from <c>Program.Main</c>, is
    /// what actually themes the app: window and control colours, menu dropdowns, disabled
    /// text, and the native chrome (scrollbars, combo drop-down buttons, edit borders) that
    /// managed properties cannot reach at all.
    ///
    /// An earlier attempt recoloured the whole control tree by hand instead, and made things
    /// *worse* — near-black menu text and disabled buttons that vanished into the
    /// background. So this class is deliberately thin and covers only the leftovers:
    ///   • the title bar, which SetColorMode does not paint;
    ///   • ListView grid lines, drawn by comctl32 in a fixed light colour with no property
    ///     to change them;
    ///   • ListView column headers, which stay light regardless.
    ///
    /// Palette values match the sibling desktopPet project so the two tools look related.
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

        private static readonly Color DarkText   = Color.FromArgb(240, 240, 240);
        private static readonly Color DarkMuted  = Color.FromArgb(170, 170, 170);
        private static readonly Color DarkBorder = Color.FromArgb(70, 70, 74);
        private static readonly Color DarkHeader = Color.FromArgb(58, 58, 61);

        public static Color Fore  => IsDark ? DarkText  : SystemColors.ControlText;
        public static Color Muted => IsDark ? DarkMuted : SystemColors.GrayText;

        /// <summary>A warning/attention colour that stays legible on either background.</summary>
        public static Color Warn => IsDark ? Color.FromArgb(255, 176, 96) : Color.FromArgb(150, 60, 0);

        /// <summary>An error colour that stays legible on either background.</summary>
        public static Color Danger => IsDark ? Color.FromArgb(255, 128, 128) : Color.FromArgb(170, 0, 0);

        // ── Application ──────────────────────────────────────────────────────

        /// <summary>Finishes a form off after WinForms' own colour mode has done the rest.</summary>
        public static void Apply(Form form)
        {
            if (form is null || form.IsDisposed) return;
            bool dark = IsDark;

            ApplyTitleBar(form, dark);
            ApplyDeep(form, dark);
        }

        private static void ApplyDeep(Control parent, bool dark)
        {
            foreach (Control child in parent.Controls)
            {
                if (child is ListView lv) ApplyListView(lv, dark);
                ApplyDeep(child, dark);
            }
        }

        /// <summary>
        /// Repaints a ToolStrip that lives outside a form's control tree — the tray's
        /// context menu, owned by the NotifyIcon. SetColorMode already renders it in the
        /// right palette; overriding its renderer here is what produced unreadable text.
        /// </summary>
        public static void Apply2(ToolStrip? strip) => strip?.Invalidate();

        /// <summary>
        /// ListView is the one control the framework's dark mode leaves half-done: its grid
        /// lines are painted by comctl32 in a fixed light colour (no property exposes it),
        /// and its column headers keep a light background. Dark mode therefore drops the
        /// grid lines and owner-draws the headers.
        /// </summary>
        private static void ApplyListView(ListView lv, bool dark)
        {
            lv.GridLines = !dark;

            // Re-applying would otherwise stack handlers.
            lv.DrawColumnHeader -= OnDrawColumnHeader;
            lv.DrawItem         -= OnDrawDefault;
            lv.DrawSubItem      -= OnDrawSubItemDefault;

            if (dark)
            {
                lv.OwnerDraw = true;
                lv.DrawColumnHeader += OnDrawColumnHeader;
                // Rows and cells stay OS-drawn — only the headers are ours. Without these
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

        private static void OnDrawDefault(object? sender, DrawListViewItemEventArgs e)
            => e.DrawDefault = true;

        private static void OnDrawSubItemDefault(object? sender, DrawListViewSubItemEventArgs e)
            => e.DrawDefault = true;

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
    }
}
