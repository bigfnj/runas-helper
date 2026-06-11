using System;
using System.Runtime.InteropServices;

namespace RunAsHelper.Service.Core;

/// <summary>
/// Acquires a TrustedInstaller token and launches processes under it.
/// Designed to run inside a Windows service (Session 0 / LocalSystem).
/// Differences from the tray version:
///   — DuplicateTokenEx uses TokenPrimary (required by CreateProcessWithTokenW).
///   — SetTokenInformation(TokenSessionId) maps the launch to the active console
///     session so the process appears on the interactive desktop.
///   — SeTcbPrivilege is enabled to allow the session-ID change.
/// </summary>
internal sealed class ElevationLauncher
{
    private volatile bool   _initialized;
    private volatile IntPtr _hElevatedToken = IntPtr.Zero;
    private          IntPtr _hNtDll   = IntPtr.Zero;

    public event Action<string>? LogMessage;

    private void Log(string msg) => LogMessage?.Invoke(msg);

    // ── Public API ───────────────────────────────────────────────────────

    public bool IsReady => _hElevatedToken != IntPtr.Zero;

    /// <summary>
    /// Runs the privilege chain and caches the TrustedInstaller token.
    /// Idempotent — safe to call from any thread, multiple times.
    /// </summary>
    public void Initialize()
    {
        if (!_initialized)
        {
            Log("Enabling privileges...");
            AdjustPrivileges();
            Log("Impersonating system...");
            if (!ImpersonateSystem()) { Log("Failed to impersonate system."); return; }
            _initialized = true;
        }

        try
        {
            if (_hElevatedToken == IntPtr.Zero)
                StartAndAcquireToken();
        }
        finally
        {
            // The token is now duplicated into _hElevatedToken (a process-owned
            // handle), so the thread no longer needs to wear the winlogon /
            // TrustedInstaller impersonation mask. Drop it before this thread
            // returns to the pool — otherwise pooled threads silently keep
            // running under an impersonated identity. The cached token still
            // works for later launches: CreateProcessWithTokenW relies on the
            // service's SeImpersonatePrivilege, not on active impersonation.
            if (!NativeMethods.RevertToSelf())
                Log($"Warning: RevertToSelf failed. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
        }
    }

    public bool LaunchElevated(string commandLine,
        uint priorityClass = NativeMethods.NORMAL_PRIORITY_CLASS)
    {
        Initialize();

        if (_hElevatedToken == IntPtr.Zero)
        {
            Log("Failed to acquire elevated token");
            return false;
        }

        Log("Duplicating elevated token...");
        IntPtr hDup;
        unsafe
        {
            var satr = new NativeMethods.SECURITY_ATTRIBUTES
            {
                nLength = (uint)sizeof(NativeMethods.SECURITY_ATTRIBUTES)
            };
            // TokenPrimary is required by CreateProcessWithTokenW.
            if (!NativeMethods.DuplicateTokenEx(
                    _hElevatedToken, NativeMethods.MAXIMUM_ALLOWED, &satr,
                    NativeMethods.SecurityImpersonationLevel.SecurityImpersonation,
                    NativeMethods.TokenType.TokenPrimary,
                    out hDup))
            {
                Log($"LaunchElevated::Failed to duplicate elevated token, " +
                    $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                return false;
            }
        }

        try
        {
            return CreateProcess(hDup, commandLine, priorityClass);
        }
        finally
        {
            NativeMethods.CloseHandle(hDup);
        }
    }

    public void ReleaseToken()
    {
        if (_hElevatedToken != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_hElevatedToken);
            _hElevatedToken = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Acquires the TrustedInstaller token (without launching anything), confirms
    /// it really belongs to NT SERVICE\TrustedInstaller, and ensures the worker
    /// thread is reverted to its own identity afterwards. Used by the tray app's
    /// post-install validation to prove the elevation chain works end to end.
    /// </summary>
    public unsafe bool ValidateToken(out string account)
    {
        account = string.Empty;
        Initialize();

        if (_hElevatedToken == IntPtr.Zero)
        {
            Log("Validation: TrustedInstaller token could not be acquired.");
            return false;
        }

        Log("Validating cached TrustedInstaller token...");

        // First call sizes the buffer (returns false + ERROR_INSUFFICIENT_BUFFER).
        NativeMethods.GetTokenInformation(
            _hElevatedToken, NativeMethods.TOKEN_INFORMATION_CLASS.TokenUser,
            null, 0, out uint len);
        if (len == 0)
        {
            Log($"Validation: GetTokenInformation(size) failed. " +
                $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
            return false;
        }

        byte* buf = stackalloc byte[(int)len];
        if (!NativeMethods.GetTokenInformation(
                _hElevatedToken, NativeMethods.TOKEN_INFORMATION_CLASS.TokenUser,
                buf, len, out _))
        {
            Log($"Validation: GetTokenInformation failed. " +
                $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
            return false;
        }

        // TOKEN_USER begins with a SID_AND_ATTRIBUTES whose first field is the SID pointer.
        IntPtr pSid = *(IntPtr*)buf;

        string sidString = "(unknown)";
        if (NativeMethods.ConvertSidToStringSidW(pSid, out IntPtr pStr) && pStr != IntPtr.Zero)
        {
            sidString = Marshal.PtrToStringUni(pStr) ?? sidString;
            NativeMethods.LocalFree(pStr);
        }

        account = LookupSid(pSid);
        Log($"Token user: {account} (SID {sidString})");

        bool isTrustedInstaller =
            string.Equals(sidString, TrustedInstallerSid, StringComparison.OrdinalIgnoreCase)
            || account.EndsWith("TrustedInstaller", StringComparison.OrdinalIgnoreCase);

        if (isTrustedInstaller)
            Log("Validation OK: token belongs to NT SERVICE\\TrustedInstaller.");
        else
            Log($"Validation FAILED: token is not TrustedInstaller (resolved {account}).");

        // Belt and braces: make sure no impersonation mask remains on this thread
        // before it returns to the pool. (Initialize already reverts; repeat here
        // so a future refactor of Initialize cannot silently leak impersonation.)
        if (!NativeMethods.RevertToSelf())
            Log($"Warning: RevertToSelf after validation failed. " +
                $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");

        return isTrustedInstaller;
    }

    // Well-known SID of NT SERVICE\TrustedInstaller.
    private const string TrustedInstallerSid =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private unsafe string LookupSid(IntPtr pSid)
    {
        char* name = stackalloc char[256];
        char* dom  = stackalloc char[256];
        uint  cchName = 256, cchDom = 256;
        if (NativeMethods.LookupAccountSidW(null, pSid, name, ref cchName, dom, ref cchDom, out _))
        {
            string d = new string(dom);
            string n = new string(name);
            return string.IsNullOrEmpty(d) ? n : $"{d}\\{n}";
        }
        return "(unresolved account)";
    }

    // ── Privilege adjustment ─────────────────────────────────────────────

    private void AdjustPrivileges()
    {
        if (!NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY,
                out IntPtr hToken))
        {
            Log("AdjustPrivileges::Failed to open process token.");
            return;
        }
        try
        {
            foreach (string priv in new[]
            {
                NativeMethods.SE_DEBUG_NAME,
                NativeMethods.SE_IMPERSONATE_NAME,
                NativeMethods.SE_TCB_NAME,      // needed for SetTokenInformation(TokenSessionId)
            })
            {
                if (SetPrivilege(hToken, priv, true))
                    Log($"AdjustPrivileges::Enabled {priv}.");
                else
                    Log($"AdjustPrivileges::Failed to enable {priv}.");
            }
        }
        finally { NativeMethods.CloseHandle(hToken); }
    }

    private unsafe bool SetPrivilege(IntPtr hToken, string privilege, bool enable)
    {
        NativeMethods.LUID luid;
        if (!NativeMethods.LookupPrivilegeValueW(null, privilege, &luid))
        {
            Log($"SetPrivilege::LookupPrivilegeValue failed for {privilege}. " +
                $"LastError={GetErrorName((uint)Marshal.GetLastWin32Error())}");
            return false;
        }

        var tp = new NativeMethods.TOKEN_PRIVILEGES
        {
            PrivilegeCount = 1,
            Privilege = new NativeMethods.LUID_AND_ATTRIBUTES
            {
                Luid       = luid,
                Attributes = enable ? NativeMethods.SE_PRIVILEGE_ENABLED : 0
            }
        };

        NativeMethods.AdjustTokenPrivileges(hToken, false, &tp, 0, null, null);

        uint err = (uint)Marshal.GetLastWin32Error();
        if (err != 0) { Log($"SetPrivilege::Error={GetErrorName(err)}"); return false; }
        return true;
    }

    // ── System impersonation ─────────────────────────────────────────────

    private bool ImpersonateSystem()
    {
        uint pidWinLogon = FindProcessByName("winlogon.exe");
        if (pidWinLogon == 0) { Log("Failed to find winlogon pid."); return false; }

        Log("Got winlogon pid, opening process...");
        IntPtr hWinLogon = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_DUP_HANDLE | NativeMethods.PROCESS_QUERY_INFORMATION,
            false, pidWinLogon);
        if (hWinLogon == IntPtr.Zero)
        {
            Log($"Failed to open winlogon. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
            return false;
        }
        try
        {
            if (!NativeMethods.OpenProcessToken(
                    hWinLogon,
                    NativeMethods.TOKEN_QUERY | NativeMethods.TOKEN_DUPLICATE,
                    out IntPtr hSysTkn))
            {
                Log($"Failed to open winlogon token. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                return false;
            }
            try
            {
                bool ok = NativeMethods.ImpersonateLoggedOnUser(hSysTkn);
                if (ok) Log("Successfully impersonated system!");
                else    Log($"Failed to impersonate. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                return ok;
            }
            finally { NativeMethods.CloseHandle(hSysTkn); }
        }
        finally { NativeMethods.CloseHandle(hWinLogon); }
    }

    // ── Elevated token acquisition ─────────────────────────────────────

    private void StartAndAcquireToken()
    {
        IntPtr hSCM = NativeMethods.OpenSCManagerW(null, null, NativeMethods.SC_MANAGER_ALL_ACCESS);
        if (hSCM == IntPtr.Zero)
        {
            Log($"Failed to open SCManager. error={GetErrorName((uint)Marshal.GetLastWin32Error())}");
            return;
        }
        try
        {
            IntPtr hSvc = NativeMethods.OpenServiceW(hSCM, "TrustedInstaller",
                NativeMethods.SERVICE_START | NativeMethods.SERVICE_QUERY_STATUS);
            if (hSvc == IntPtr.Zero)
            {
                Log($"Failed to open TrustedInstaller service. error={GetErrorName((uint)Marshal.GetLastWin32Error())}");
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
        Log("Waiting for TrustedInstaller service...");
        uint svcSize = (uint)sizeof(NativeMethods.SERVICE_STATUS_PROCESS);

        while (NativeMethods.QueryServiceStatusEx(
            hSvc, NativeMethods.SC_STATUS_PROCESS_INFO,
            out NativeMethods.SERVICE_STATUS_PROCESS st, svcSize, out _))
        {
            switch (st.dwCurrentState)
            {
                case NativeMethods.ServiceState.Stopped:
                    Log("Service stopped, starting...");
                    if (!NativeMethods.StartServiceW(hSvc, 0, IntPtr.Zero))
                    {
                        uint err = (uint)Marshal.GetLastWin32Error();
                        if (err != NativeMethods.ERROR_SERVICE_ALREADY_RUNNING)
                        {
                            Log($"Error starting TrustedInstaller. error={GetErrorName(err)}");
                            return 0;
                        }
                    }
                    break;

                case NativeMethods.ServiceState.StartPending:
                case NativeMethods.ServiceState.StopPending:
                    Log($"Service pending, waiting {st.dwWaitHint}ms...");
                    NativeMethods.Sleep(st.dwWaitHint);
                    break;

                case NativeMethods.ServiceState.Running:
                    Log($"TrustedInstaller running, pid={st.dwProcessId}.");
                    return st.dwProcessId;
            }
        }
        return 0;
    }

    private unsafe void AcquireTokenFromProcess(uint pid)
    {
        uint tid = GetFirstThreadId(pid);
        Log($"Elevated thread id={tid}");
        if (tid == 0) { Log("Failed to get elevated thread id."); return; }

        IntPtr hThread = NativeMethods.OpenThread(
            NativeMethods.THREAD_DIRECT_IMPERSONATION, false, tid);
        if (hThread == IntPtr.Zero)
        {
            Log($"Failed to open elevated thread. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
            return;
        }
        try
        {
            var sqos = new NativeMethods.SECURITY_QUALITY_OF_SERVICE
            {
                Length             = (uint)sizeof(NativeMethods.SECURITY_QUALITY_OF_SERVICE),
                ImpersonationLevel = NativeMethods.SecurityImpersonationLevel.SecurityImpersonation,
            };

            int status = NativeMethods.NtImpersonateThread(
                NativeMethods.GetCurrentThread(), hThread, &sqos);

            if (status != NativeMethods.STATUS_SUCCESS)
            {
                Log($"NtImpersonateThread failed, NTSTATUS={GetNtStatusName(status)}");
                return;
            }

            Log("NtImpersonateThread STATUS_SUCCESS. Opening thread token...");
            // Open into a local first: a volatile field cannot be passed as an
            // 'out' argument without losing its volatile semantics (CS0420).
            if (NativeMethods.OpenThreadToken(
                    NativeMethods.GetCurrentThread(),
                    NativeMethods.TOKEN_ALL_ACCESS,
                    false, out IntPtr hToken))
            {
                _hElevatedToken = hToken;
                Log("Token acquired and cached.");
            }
            else
                Log($"OpenThreadToken failed. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
        }
        finally { NativeMethods.CloseHandle(hThread); }
    }

    // ── Process creation (session-aware for service context) ─────────────

    private unsafe bool CreateProcess(IntPtr hToken, string commandLine, uint priorityClass)
    {
        Log("Creating process...");

        // Remap the token to the active console session so the launched process
        // appears on the interactive desktop instead of the invisible Session 0.
        uint sessionId = NativeMethods.WTSGetActiveConsoleSessionId();
        if (sessionId != uint.MaxValue)
        {
            if (!NativeMethods.SetTokenInformation(
                    hToken,
                    NativeMethods.TOKEN_INFORMATION_CLASS.TokenSessionId,
                    &sessionId,
                    sizeof(uint)))
            {
                Log($"Warning: SetTokenInformation(TokenSessionId) failed. " +
                    $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
            }
            else
            {
                Log($"Token session remapped to session {sessionId}.");
            }
        }

        var (app, args) = ParseCommandLine(commandLine);

        // Console-subsystem programs (cmd, powershell) launched from a service
        // get no usable window unless we allocate a fresh console. GUI apps
        // (regedit, notepad) must NOT get one, or an empty console flashes up.
        uint creationFlags = NativeMethods.CREATE_UNICODE_ENVIRONMENT | priorityClass;
        if (IsConsoleSubsystem(string.IsNullOrEmpty(args) ? commandLine : app))
        {
            creationFlags |= NativeMethods.CREATE_NEW_CONSOLE;
            Log("Console application detected — allocating an interactive console window.");
        }

        fixed (char* pDesktop = "WinSta0\\Default")
        {
            var si = new NativeMethods.STARTUPINFOW
            {
                cb        = (uint)sizeof(NativeMethods.STARTUPINFOW),
                lpDesktop = (IntPtr)pDesktop,
            };

            bool result;
            if (string.IsNullOrEmpty(args))
            {
                if (commandLine.Contains('%'))
                    commandLine = ExpandEnvVars(commandLine);

                result = NativeMethods.CreateProcessWithTokenW(
                    hToken, NativeMethods.LOGON_WITH_PROFILE,
                    null, commandLine,
                    creationFlags,
                    IntPtr.Zero, null, &si, out _);
            }
            else
            {
                Log($"Args detected — app={app}  args={args}");
                if (app.Contains('%')) app = ExpandEnvVars(app);

                result = NativeMethods.CreateProcessWithTokenW(
                    hToken, NativeMethods.LOGON_WITH_PROFILE,
                    app, commandLine,
                    creationFlags,
                    IntPtr.Zero, null, &si, out _);
            }

            if (!result)
                Log($"CreateProcessWithTokenW failed. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
            return result;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private unsafe uint FindProcessByName(string name)
    {
        IntPtr hSnap = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (hSnap == IntPtr.Zero) return 0;
        uint result = 0;
        try
        {
            NativeMethods.PROCESSENTRY32W e = new()
            { dwSize = (uint)sizeof(NativeMethods.PROCESSENTRY32W) };
            if (!NativeMethods.Process32FirstW(hSnap, &e)) return 0;
            do
            {
                if (string.Equals(e.ExeName, name, StringComparison.OrdinalIgnoreCase))
                { result = e.th32ProcessID; break; }
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
            { dwSize = (uint)sizeof(NativeMethods.THREADENTRY32) };
            if (NativeMethods.Thread32First(hSnap, &e))
            {
                do
                {
                    if (e.th32OwnerProcessID == pid) { result = e.th32ThreadID; break; }
                }
                while (NativeMethods.Thread32Next(hSnap, &e));
            }
        }
        finally { NativeMethods.CloseHandle(hSnap); }
        return result;
    }

    private static (string app, string args) ParseCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return (commandLine, string.Empty);
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

    // True if the launch target is a console-subsystem executable, so it needs
    // its own console window. Resolves bare names (e.g. "powershell.exe") via
    // the standard search path, then reads the PE optional-header subsystem.
    // Best-effort: any failure returns false (GUI behaviour, i.e. no new console).
    private unsafe bool IsConsoleSubsystem(string app)
    {
        try
        {
            string exe = app.Trim().Trim('"');
            if (exe.Length == 0) return false;
            if (exe.Contains('%')) exe = ExpandEnvVars(exe);

            string full = exe;
            bool hasPath = exe.Contains('\\') || exe.Contains('/');
            if (!hasPath)
            {
                const int n = NativeMethods.MAX_PATH * 2;
                char* buf = stackalloc char[n];
                uint len = NativeMethods.SearchPathW(null, exe, ".exe", n, buf, IntPtr.Zero);
                if (len == 0 || len >= n) return false;
                full = new string(buf, 0, (int)len);
            }

            if (!System.IO.File.Exists(full)) return false;
            return ReadPeSubsystem(full) == NativeMethods.IMAGE_SUBSYSTEM_WINDOWS_CUI;
        }
        catch { return false; }
    }

    private static ushort ReadPeSubsystem(string path)
    {
        using var fs = System.IO.File.OpenRead(path);
        using var br = new System.IO.BinaryReader(fs);

        if (br.ReadUInt16() != 0x5A4D) return 0;          // 'MZ'
        fs.Position = 0x3C;
        int peOffset = br.ReadInt32();
        fs.Position = peOffset;
        if (br.ReadUInt32() != 0x0000_4550) return 0;      // 'PE\0\0'

        // Subsystem is a WORD at the same offset (68) in the optional header for
        // both PE32 and PE32+. Optional header starts after the 20-byte COFF
        // file header that follows the 4-byte PE signature.
        fs.Position = peOffset + 4 + 20 + 68;
        return br.ReadUInt16();
    }

    private static unsafe string ExpandEnvVars(string input)
    {
        const int BufSize = NativeMethods.MAX_PATH * 4;
        char* buf = stackalloc char[BufSize];
        uint  n   = NativeMethods.ExpandEnvironmentStringsW(input, buf, BufSize);
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
        string msg = n > 0 ? new string(buf, 0, (int)n).TrimEnd('\r', '\n') : string.Empty;
        return $"0x{error:X8} - {msg}";
    }

    private unsafe string GetNtStatusName(int status)
    {
        if (_hNtDll == IntPtr.Zero)
            _hNtDll = NativeMethods.LoadLibraryW("ntdll.dll");
        if (_hNtDll == IntPtr.Zero) return $"0x{status:X8}";

        const int BufSize = 1024;
        char* buf = stackalloc char[BufSize];
        uint  n   = NativeMethods.FormatMessageW(
            NativeMethods.FORMAT_MESSAGE_FROM_HMODULE |
            NativeMethods.FORMAT_MESSAGE_IGNORE_INSERTS,
            _hNtDll, (uint)status, 0, buf, BufSize, IntPtr.Zero);
        string msg = n > 0 ? new string(buf, 0, (int)n).TrimEnd('\r', '\n') : string.Empty;
        return $"0x{status:X8} - {msg}";
    }
}
