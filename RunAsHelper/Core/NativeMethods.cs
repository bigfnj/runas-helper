using System;
using System.Runtime.InteropServices;

namespace RunAsHelper.Core
{
    internal static partial class NativeMethods
    {
        // ── Priority classes ──────────────────────────────────────────────────
        internal const uint IDLE_PRIORITY_CLASS         = 0x00000040;
        internal const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
        internal const uint NORMAL_PRIORITY_CLASS       = 0x00000020;
        internal const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
        internal const uint HIGH_PRIORITY_CLASS         = 0x00000080;
        internal const uint REALTIME_PRIORITY_CLASS     = 0x00000100;

        internal static string PriorityClassName(uint priority) => priority switch
        {
            IDLE_PRIORITY_CLASS         => "Idle",
            BELOW_NORMAL_PRIORITY_CLASS => "Below Normal",
            NORMAL_PRIORITY_CLASS       => "Normal",
            ABOVE_NORMAL_PRIORITY_CLASS => "Above Normal",
            HIGH_PRIORITY_CLASS         => "High",
            REALTIME_PRIORITY_CLASS     => "Realtime",
            _                           => "Normal",
        };

        // ── Button shield ─────────────────────────────────────────────────────
        internal const uint BCM_FIRST     = 0x1600;
        internal const uint BCM_SETSHIELD = BCM_FIRST + 0x000C;

        // ── P/Invoke ──────────────────────────────────────────────────────────

        // Checks whether a named pipe with the given name exists (non-connecting).
        // Returns true when the service pipe is present.
        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool WaitNamedPipeW(string lpNamedPipeName, uint nTimeOut);

        [LibraryImport("shell32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsUserAnAdmin();

        [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
        internal static partial IntPtr SendMessage(
            IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        // Releases a GDI HICON produced by Bitmap.GetHicon().
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyIcon(IntPtr hIcon);

        // ── Installer lookup (post-install validation recovery) ───────────────

        // Finds an installed ProductCode from the stable UpgradeCode so the
        // validation dialog's Repair/Uninstall actions can target this install.
        [LibraryImport("msi.dll", StringMarshalling = StringMarshalling.Utf16)]
        internal static unsafe partial uint MsiEnumRelatedProductsW(
            string lpUpgradeCode, uint dwReserved, uint iProductIndex, char* lpProductBuf);

        /// <summary>
        /// Returns the ProductCode GUID of the installed product registered under
        /// <paramref name="upgradeCode"/>, or null if it is not installed.
        /// </summary>
        internal static unsafe string? FindInstalledProductCode(string upgradeCode)
        {
            const uint ErrorSuccess = 0;
            // A ProductCode GUID is always 38 characters plus a null terminator.
            char* buf = stackalloc char[40];
            return MsiEnumRelatedProductsW(upgradeCode, 0, 0, buf) == ErrorSuccess
                ? new string(buf)
                : null;
        }
    }
}
