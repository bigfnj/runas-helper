using System;
using System.IO;
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

        // Attach to the launching console so CLI/help output is visible (the app
        // is a WinExe and has no console of its own).
        internal const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool AttachConsole(uint dwProcessId);

        [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
        internal static partial IntPtr SendMessage(
            IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        // Resolves a bare name (e.g. "lusrmgr.msc") via the standard search order
        // (current dir, System32, Windows, PATH). Used to validate a saved app.
        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static unsafe partial uint SearchPathW(
            string? lpPath, string lpFileName, string? lpExtension,
            uint nBufferLength, char* lpBuffer, IntPtr lpFilePart);

        // ── Document associations ────────────────────────────────────────
        //
        // File associations are PER-USER: the default app lives under the user's hive
        // (FileExts\<ext>\UserChoice). The service runs as SYSTEM, which has no such
        // choice, so resolving there yields the OpenWith.exe "how do you want to open
        // this?" fallback instead of the real handler. Resolution therefore happens
        // here, in the client, which runs as the actual user.
        internal const uint ASSOCF_NONE         = 0;
        internal const uint ASSOCSTR_COMMAND    = 1;
        internal const uint ASSOCSTR_EXECUTABLE = 2;

        [LibraryImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", StringMarshalling = StringMarshalling.Utf16)]
        internal static unsafe partial int AssocQueryStringW(
            uint     flags,
            uint     str,
            string   pszAssoc,
            string?  pszExtra,
            char*    pszOut,
            ref uint pcchOut);

        /// <summary>
        /// The command that opens <paramref name="path"/> with its registered handler, or
        /// null when the file type has no usable handler. Returns null for the OpenWith
        /// picker too: launching that elevated would just show a chooser dialog running as
        /// SYSTEM, which is worse than reporting that nothing is registered.
        /// </summary>
        internal static unsafe string? ResolveDocumentCommand(string path)
        {
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return null;

            string? command = AssocQuery(ASSOCSTR_COMMAND, ext);
            if (!string.IsNullOrWhiteSpace(command) && !IsOpenWithPicker(command!))
                return SubstituteShellArgs(command!, path);

            string? exe = AssocQuery(ASSOCSTR_EXECUTABLE, ext);
            if (!string.IsNullOrWhiteSpace(exe) && !IsOpenWithPicker(exe!))
                return $"\"{exe}\" \"{path}\"";

            return null;
        }

        private static bool IsOpenWithPicker(string command)
            => command.Contains("OpenWith.exe", StringComparison.OrdinalIgnoreCase);

        private static unsafe string? AssocQuery(uint what, string ext)
        {
            uint len = 0;
            if (AssocQueryStringW(ASSOCF_NONE, what, ext, null, null, ref len) < 0 || len == 0 || len > 4096)
                return null;

            char* buf = stackalloc char[(int)len];
            if (AssocQueryStringW(ASSOCF_NONE, what, ext, null, buf, ref len) != 0)
                return null;

            string s = new string(buf).TrimEnd('\0').Trim();
            return s.Length == 0 ? null : s;
        }

        // Fills in a registered command's parameters: %1/%L become the file; the other
        // shell placeholders are dropped rather than passed through literally.
        private static string SubstituteShellArgs(string command, string path)
        {
            string quoted = $"\"{path}\"";
            bool substituted = false;

            foreach (string token in new[] { "\"%1\"", "\"%L\"", "\"%l\"", "%1", "%L", "%l" })
            {
                if (command.Contains(token, StringComparison.Ordinal))
                {
                    command = command.Replace(token, quoted, StringComparison.Ordinal);
                    substituted = true;
                    break;
                }
            }

            foreach (string extra in new[] { "%*", "%2", "%3", "%4", "%5", "%6", "%7", "%8", "%9",
                                             "\"%I\"", "%I", "\"%i\"", "%i", "%D", "%d", "%W", "%w", "%v", "%V" })
                command = command.Replace(extra, string.Empty, StringComparison.Ordinal);

            command = command.Trim();
            return substituted ? command : $"{command} {quoted}";
        }

        /// <summary>
        /// Resolves a file location the way a launch would find it: rooted/relative
        /// paths via existence, bare names via the PATH search order. Returns the
        /// resolved full path, or null if it cannot be found.
        /// </summary>
        internal static unsafe string? ResolvePath(string location)
        {
            if (string.IsNullOrWhiteSpace(location)) return null;
            string p = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'));

            // Rooted or contains a directory separator → treat as a direct path.
            if (Path.IsPathRooted(p) || p.Contains('\\') || p.Contains('/'))
            {
                try { return File.Exists(p) ? Path.GetFullPath(p) : null; }
                catch { return null; }
            }

            // Bare name → search PATH (+ System32, Windows, current directory).
            const int n = 1024;
            char* buf = stackalloc char[n];
            uint len = SearchPathW(null, p, null, n, buf, IntPtr.Zero);
            return len > 0 && len < n ? new string(buf, 0, (int)len) : null;
        }

        // Releases a GDI HICON produced by Bitmap.GetHicon().
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyIcon(IntPtr hIcon);

        // Grants a process (by PID) the right to call SetForegroundWindow. The tray
        // calls this with the PID returned by the service after a successful launch so
        // the newly-spawned elevated process can bring itself to the foreground.
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool AllowSetForegroundWindow(uint dwProcessId);

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
