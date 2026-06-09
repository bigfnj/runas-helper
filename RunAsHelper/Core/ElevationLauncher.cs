using System;
using System.Runtime.InteropServices;

namespace RunAsHelper.Core
{
    /// <summary>
    /// Port of modRunAsHelper (twinBASIC v2.3.2).
    /// Launches a process running as NT AUTHORITY\SYSTEM with TrustedInstaller privileges.
    /// Requires the caller to already be elevated (Administrator).
    /// </summary>
    internal sealed class ElevationLauncher
    {
        // volatile: written on the background init thread, read on the UI thread.
        private volatile bool   _initialized;
        private volatile IntPtr _hElevatedToken = IntPtr.Zero;
        private          IntPtr _hNtDll   = IntPtr.Zero;

        /// <summary>Raised for each progress/error message during an operation.</summary>
        public event Action<string>? LogMessage;

        private void Log(string msg) => LogMessage?.Invoke(msg);

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// True once the TrustedInstaller token has been acquired and cached.
        /// Safe to read from any thread.
        /// </summary>
        public bool IsReady => _hElevatedToken != IntPtr.Zero;

        /// <summary>
        /// Run the full init chain (privilege elevation → winlogon impersonation →
        /// TI service start → NtImpersonateThread → token acquisition) and cache
        /// the resulting token for the lifetime of this instance.
        ///
        /// Idempotent: safe to call multiple times or from a background thread.
        /// After this returns successfully, <see cref="IsReady"/> is true and every
        /// subsequent <see cref="LaunchElevated"/> call bypasses the expensive chain.
        /// </summary>
        public void Initialize()
        {
            if (!_initialized)
            {
                Log("Enabling privileges...");
                AdjustPrivileges();
                Log("Impersonating system...");
                if (!ImpersonateSystem())
                {
                    Log("Failed to impersonate system.");
                    return;
                }
                _initialized = true;
            }

            if (_hElevatedToken == IntPtr.Zero)
                StartAndAcquireToken();
        }

        /// <summary>
        /// Launch <paramref name="commandLine"/> as TrustedInstaller.
        /// Calls <see cref="Initialize"/> automatically if the token is not yet warm.
        /// </summary>
        public bool LaunchElevated(string commandLine,
            uint priorityClass = NativeMethods.NORMAL_PRIORITY_CLASS)
        {
            Initialize();

            if (_hElevatedToken == IntPtr.Zero)
            {
                Log("Token hijack failed :(");
                return false;
            }

            Log("Duplicating stolen TI token...");
            IntPtr hStolenToken;
            unsafe
            {
                var satr = new NativeMethods.SECURITY_ATTRIBUTES
                {
                    nLength = (uint)sizeof(NativeMethods.SECURITY_ATTRIBUTES)
                };
                if (!NativeMethods.DuplicateTokenEx(
                        _hElevatedToken, NativeMethods.MAXIMUM_ALLOWED, &satr,
                        NativeMethods.SecurityImpersonationLevel.SecurityImpersonation,
                        NativeMethods.TokenType.TokenImpersonation,
                        out hStolenToken))
                {
                    Log($"LaunchElevated::Failed to duplicate TI token, " +
                        $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                    return false;
                }
            }

            try
            {
                return CreateProcess(hStolenToken, commandLine, priorityClass);
            }
            finally
            {
                NativeMethods.CloseHandle(hStolenToken);
            }
        }

        /// <summary>Enables SeDebugPrivilege and SeImpersonatePrivilege on the current process.</summary>
        public void AdjustPrivileges()
        {
            if (!NativeMethods.OpenProcessToken(
                    NativeMethods.GetCurrentProcess(),
                    NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY,
                    out IntPtr hToken))
            {
                Log("AdjustPrivileges::Failed to open process token.");
                return;
            }

            Log("AdjustPrivileges::Got process token.");
            try
            {
                if (SetPrivilege(hToken, NativeMethods.SE_DEBUG_NAME, true))
                    Log("AdjustPrivileges::Enabled debug privilege.");
                else
                    Log("AdjustPrivileges::Failed to enable debug privilege.");

                if (SetPrivilege(hToken, NativeMethods.SE_IMPERSONATE_NAME, true))
                    Log("AdjustPrivileges::Enabled impersonate privilege.");
                else
                    Log("AdjustPrivileges::Failed to enable impersonate privilege.");
            }
            finally
            {
                NativeMethods.CloseHandle(hToken);
            }
        }

        /// <summary>Closes the cached TrustedInstaller token. Next LaunchElevated call re-initializes.</summary>
        public void ReleaseToken()
        {
            if (_hElevatedToken != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_hElevatedToken);
                _hElevatedToken = IntPtr.Zero;
            }
        }

        /// <summary>Enable or disable a named privilege on <paramref name="hToken"/>.</summary>
        public unsafe bool SetPrivilege(IntPtr hToken, string privilege, bool enable)
        {
            NativeMethods.LUID luid;
            if (!NativeMethods.LookupPrivilegeValueW(null, privilege, &luid))
            {
                Log($"SetPrivilege::LookupPrivilegeValue failed. " +
                    $"LastDllError={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                return false;
            }

            var tp = new NativeMethods.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privilege      = new NativeMethods.LUID_AND_ATTRIBUTES
                {
                    Luid       = luid,
                    Attributes = enable ? NativeMethods.SE_PRIVILEGE_ENABLED : 0
                }
            };

            NativeMethods.AdjustTokenPrivileges(hToken, false, &tp, 0, null, null);

            uint err = (uint)Marshal.GetLastWin32Error();
            if (err != 0)
            {
                Log($"SetPrivilege::Error code={GetErrorName(err)}");
                return false;
            }
            return true;
        }

        // ── Private helpers ──────────────────────────────────────────────────

        private bool ImpersonateSystem()
        {
            uint pidWinLogon = FindProcessByName("winlogon.exe");
            if (pidWinLogon == 0)
            {
                Log("Failed to find winlogon processid");
                return false;
            }

            Log("Got winlogon pid, opening process...");
            IntPtr hWinLogon = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_DUP_HANDLE | NativeMethods.PROCESS_QUERY_INFORMATION,
                false, pidWinLogon);
            if (hWinLogon == IntPtr.Zero)
            {
                Log($"Failed to open winlogon process, " +
                    $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                return false;
            }

            try
            {
                Log("Got winlogon process handle, opening token...");
                if (!NativeMethods.OpenProcessToken(
                        hWinLogon,
                        NativeMethods.TOKEN_QUERY | NativeMethods.TOKEN_DUPLICATE,
                        out IntPtr hSysTkn))
                {
                    Log($"Failed to open winlogon process token. " +
                        $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                    return false;
                }
                try
                {
                    bool ok = NativeMethods.ImpersonateLoggedOnUser(hSysTkn);
                    if (ok) Log("Successfully impersonated system!");
                    else    Log($"Failed to impersonate system. " +
                                $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                    return ok;
                }
                finally { NativeMethods.CloseHandle(hSysTkn); }
            }
            finally { NativeMethods.CloseHandle(hWinLogon); }
        }

        private void StartAndAcquireToken()
        {
            IntPtr hSCM = NativeMethods.OpenSCManagerW(null, null, NativeMethods.SC_MANAGER_ALL_ACCESS);
            if (hSCM == IntPtr.Zero)
            {
                Log($"Failed to open SCManager, " +
                    $"error={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                return;
            }
            try
            {
                Log("Service manager opened. Opening TrustedInstaller service...");
                IntPtr hSvc = NativeMethods.OpenServiceW(hSCM, "TrustedInstaller",
                    NativeMethods.SERVICE_START | NativeMethods.SERVICE_QUERY_STATUS);
                if (hSvc == IntPtr.Zero)
                {
                    Log($"Failed to open TrustedInstaller service handle, " +
                        $"error={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                    return;
                }
                try
                {
                    uint pid = WaitForServiceRunning(hSvc);
                    if (pid != 0) AcquireTokenFromProcess(pid);
                }
                finally { NativeMethods.CloseHandle(hSvc); }
            }
            finally { NativeMethods.CloseHandle(hSCM); }
        }

        private unsafe uint WaitForServiceRunning(IntPtr hSvc)
        {
            Log("Attempting to start TrustedInstaller service...");
            uint svcSize = (uint)sizeof(NativeMethods.SERVICE_STATUS_PROCESS);

            while (NativeMethods.QueryServiceStatusEx(
                hSvc, NativeMethods.SC_STATUS_PROCESS_INFO,
                out NativeMethods.SERVICE_STATUS_PROCESS st, svcSize, out _))
            {
                switch (st.dwCurrentState)
                {
                    case NativeMethods.ServiceState.Stopped:
                        Log("Service currently stopped, starting...");
                        if (!NativeMethods.StartServiceW(hSvc, 0, IntPtr.Zero))
                        {
                            uint err = (uint)Marshal.GetLastWin32Error();
                            if (err != NativeMethods.ERROR_SERVICE_ALREADY_RUNNING)
                            {
                                Log($"Error starting TrustedInstaller service, " +
                                    $"error={GetErrorName(err)}");
                                return 0;
                            }
                        }
                        break;

                    case NativeMethods.ServiceState.StartPending:
                    case NativeMethods.ServiceState.StopPending:
                        Log($"Service start pending, waiting {st.dwWaitHint}ms");
                        NativeMethods.Sleep(st.dwWaitHint);
                        break;

                    case NativeMethods.ServiceState.Running:
                        Log($"Service running, pid={st.dwProcessId}");
                        return st.dwProcessId;
                }
            }
            return 0;
        }

        private unsafe void AcquireTokenFromProcess(uint pid)
        {
            uint tid = GetFirstThreadId(pid);
            Log($"First thread id for pid={tid}");
            if (tid == 0)
            {
                Log("Failed to get TrustedInstaller thread id");
                return;
            }

            IntPtr hThread = NativeMethods.OpenThread(
                NativeMethods.THREAD_DIRECT_IMPERSONATION, false, tid);
            if (hThread == IntPtr.Zero)
            {
                Log($"Failed to open TrustedInstaller thread, " +
                    $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                return;
            }
            try
            {
                var sqos = new NativeMethods.SECURITY_QUALITY_OF_SERVICE
                {
                    Length             = (uint)sizeof(NativeMethods.SECURITY_QUALITY_OF_SERVICE),
                    ImpersonationLevel = NativeMethods.SecurityImpersonationLevel.SecurityImpersonation
                };

                int status = NativeMethods.NtImpersonateThread(
                    NativeMethods.GetCurrentThread(), hThread, &sqos);

                if (status != NativeMethods.STATUS_SUCCESS)
                {
                    Log($"NtImpersonateThread failed, NTSTATUS={GetNtStatusName(status)}");
                    return;
                }

                Log("NtImpersonateThread STATUS_SUCCESS. Opening current token...");
                if (NativeMethods.OpenThreadToken(
                        NativeMethods.GetCurrentThread(),
                        NativeMethods.TOKEN_ALL_ACCESS,
                        false, out _hElevatedToken))
                {
                    Log("OpenThreadToken success.");
                }
                else
                {
                    Log($"Failed to open own token after NtIT, " +
                        $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                }
            }
            finally { NativeMethods.CloseHandle(hThread); }
        }

        private unsafe bool CreateProcess(
            IntPtr hToken, string commandLine, uint priorityClass)
        {
            Log("Token duplicated. Creating process...");
            var (app, args) = ParseCommandLine(commandLine);

            // Pin "WinSta0\Default" for the duration of the CreateProcess call.
            fixed (char* pDesktop = "WinSta0\\Default")
            {
                var si = new NativeMethods.STARTUPINFOW
                {
                    cb        = (uint)sizeof(NativeMethods.STARTUPINFOW),
                    lpDesktop = (IntPtr)pDesktop
                };

                bool result;
                if (string.IsNullOrEmpty(args))
                {
                    if (commandLine.Contains('%'))
                        commandLine = ExpandEnvVars(commandLine);

                    result = NativeMethods.CreateProcessWithTokenW(
                        hToken, NativeMethods.LOGON_WITH_PROFILE,
                        null, commandLine,
                        NativeMethods.CREATE_UNICODE_ENVIRONMENT | priorityClass,
                        IntPtr.Zero, null, &si, out _);
                }
                else
                {
                    Log("Command line args detected, parsed as:");
                    Log($"  App={app}");
                    Log($"  Arg={args}");
                    if (app.Contains('%'))
                        app = ExpandEnvVars(app);

                    result = NativeMethods.CreateProcessWithTokenW(
                        hToken, NativeMethods.LOGON_WITH_PROFILE,
                        app, commandLine,
                        NativeMethods.CREATE_UNICODE_ENVIRONMENT | priorityClass,
                        IntPtr.Zero, null, &si, out _);
                }

                if (!result)
                    Log($"LaunchElevated::CreateProcessWithTokenW failed, " +
                        $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                return result;
            }
        }

        private unsafe uint FindProcessByName(string name)
        {
            IntPtr hSnap = NativeMethods.CreateToolhelp32Snapshot(
                NativeMethods.TH32CS_SNAPPROCESS, 0);
            if (hSnap == IntPtr.Zero) return 0;

            uint result = 0;
            try
            {
                NativeMethods.PROCESSENTRY32W e = new()
                {
                    dwSize = (uint)sizeof(NativeMethods.PROCESSENTRY32W)
                };
                if (!NativeMethods.Process32FirstW(hSnap, &e))
                {
                    Log("FindProcessByName->Process32First failed.");
                    return 0;
                }
                do
                {
                    if (string.Equals(e.ExeName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        result = e.th32ProcessID;
                        break;
                    }
                }
                while (NativeMethods.Process32NextW(hSnap, &e));
            }
            finally { NativeMethods.CloseHandle(hSnap); }
            return result;
        }

        private unsafe uint GetFirstThreadId(uint pid)
        {
            IntPtr hSnap = NativeMethods.CreateToolhelp32Snapshot(
                NativeMethods.TH32CS_SNAPTHREAD, 0);
            if (hSnap == IntPtr.Zero) return 0;

            uint result = 0;
            try
            {
                NativeMethods.THREADENTRY32 e = new()
                {
                    dwSize = (uint)sizeof(NativeMethods.THREADENTRY32)
                };
                if (NativeMethods.Thread32First(hSnap, &e))
                {
                    do
                    {
                        if (e.th32OwnerProcessID == pid)
                        {
                            result = e.th32ThreadID;
                            break;
                        }
                    }
                    while (NativeMethods.Thread32Next(hSnap, &e));
                }
            }
            finally { NativeMethods.CloseHandle(hSnap); }
            return result;
        }

        // Separates exe path from arguments, handling quoted paths.
        private static (string app, string args) ParseCommandLine(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return (commandLine, string.Empty);

            string s = commandLine.Trim();
            if (s.StartsWith('"'))
            {
                int close = s.IndexOf('"', 1);
                if (close < 0) return (s[1..], string.Empty);
                return (s[1..close], s[(close + 1)..].TrimStart());
            }
            int sp = s.IndexOf(' ');
            return sp < 0 ? (s, string.Empty) : (s[..sp], s[(sp + 1)..]);
        }

        // stackalloc avoids a heap allocation for the output buffer.
        private static unsafe string ExpandEnvVars(string input)
        {
            const int BufSize = NativeMethods.MAX_PATH * 4;
            char* buf = stackalloc char[BufSize];
            uint  n   = NativeMethods.ExpandEnvironmentStringsW(input, buf, BufSize);
            // n includes the null terminator; subtract it.
            return n > 1 ? new string(buf, 0, (int)(n - 1)) : input;
        }

        private unsafe string GetErrorName(uint error)
        {
            const int BufSize = 1024;
            char* buf = stackalloc char[BufSize];
            uint  n   = NativeMethods.FormatMessageW(
                NativeMethods.FORMAT_MESSAGE_FROM_SYSTEM |
                NativeMethods.FORMAT_MESSAGE_IGNORE_INSERTS,
                IntPtr.Zero, error, 0, buf, BufSize, IntPtr.Zero);
            string msg = n > 0
                ? new string(buf, 0, (int)n).TrimEnd('\r', '\n')
                : string.Empty;
            return $"0x{error:X8} - {msg}";
        }

        private unsafe string GetNtStatusName(int status)
        {
            if (_hNtDll == IntPtr.Zero)
                _hNtDll = NativeMethods.LoadLibraryW("ntdll.dll");
            if (_hNtDll == IntPtr.Zero)
                return $"0x{status:X8}";

            const int BufSize = 1024;
            char* buf = stackalloc char[BufSize];
            uint  n   = NativeMethods.FormatMessageW(
                NativeMethods.FORMAT_MESSAGE_FROM_HMODULE |
                NativeMethods.FORMAT_MESSAGE_IGNORE_INSERTS,
                _hNtDll, (uint)status, 0, buf, BufSize, IntPtr.Zero);
            string msg = n > 0
                ? new string(buf, 0, (int)n).TrimEnd('\r', '\n')
                : string.Empty;
            return $"0x{status:X8} - {msg}";
        }
    }
}
