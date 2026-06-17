using System;
using System.Runtime.InteropServices;

namespace RunAsHelper.Service.Core;

/// <summary>
/// Acquires a TrustedInstaller token and launches processes under it.
/// Designed to run inside a Windows service (Session 0 / LocalSystem).
/// Differences from the tray version:
///   — DuplicateTokenEx uses TokenPrimary (required by CreateProcessAsUserW).
///   — SetTokenInformation(TokenSessionId) maps the launch to the requesting
///     client session so the process appears on the user's interactive desktop.
///   — SeTcbPrivilege is enabled to allow the session-ID change.
/// </summary>
internal sealed class ElevationLauncher
{
    private volatile bool   _initialized;
    private volatile bool   _privilegesAdjusted;
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
        EnsurePrivileges();
        if (!_initialized)
        {
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
            // works for later launches: CreateProcessAsUserW relies on the
            // service's SeImpersonatePrivilege, not on active impersonation.
            if (!NativeMethods.RevertToSelf())
                Log($"Warning: RevertToSelf failed. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
            else
                Log("Worker thread reverted to self (impersonation dropped after token clone).");
        }
    }

    // Returns the PID of the launched process, or 0 on failure.
    public uint LaunchElevated(string commandLine,
        uint priorityClass = NativeMethods.NORMAL_PRIORITY_CLASS,
        string? workingDirectory = null,
        int showWindow = NativeMethods.SW_SHOWNORMAL,
        string account = "ti",
        uint? targetSessionId = null)
    {
        bool asSystem = string.Equals(account, "system", StringComparison.OrdinalIgnoreCase);

        // Pick the source token: the LocalSystem (service) token for account=system
        // — a pure SYSTEM token with no TrustedInstaller group — or the stolen
        // TrustedInstaller token (SYSTEM + TI group) otherwise.
        IntPtr source;
        bool   closeSource = false;
        if (asSystem)
        {
            EnsurePrivileges();
            if (!NativeMethods.OpenProcessToken(
                    NativeMethods.GetCurrentProcess(),
                    NativeMethods.TOKEN_DUPLICATE | NativeMethods.TOKEN_QUERY, out source))
            {
                Log($"Failed to open LocalSystem token. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                return 0;
            }
            closeSource = true;
            Log("Account=system — launching with the LocalSystem token (no TrustedInstaller group).");
        }
        else
        {
            Initialize();
            if (_hElevatedToken == IntPtr.Zero)
            {
                Log("Failed to acquire elevated token");
                return 0;
            }
            source = _hElevatedToken;
            Log("Account=trustedinstaller — launching with the TrustedInstaller token.");
        }

        try
        {
            Log("Duplicating token...");
            IntPtr hDup;
            unsafe
            {
                var satr = new NativeMethods.SECURITY_ATTRIBUTES
                {
                    nLength = (uint)sizeof(NativeMethods.SECURITY_ATTRIBUTES)
                };
                // TokenPrimary is required by CreateProcessAsUser.
                if (!NativeMethods.DuplicateTokenEx(
                        source, NativeMethods.MAXIMUM_ALLOWED, &satr,
                        NativeMethods.SecurityImpersonationLevel.SecurityImpersonation,
                        NativeMethods.TokenType.TokenPrimary,
                        out hDup))
                {
                    Log($"LaunchElevated::Failed to duplicate token, " +
                        $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                    return 0;
                }
            }

            try
            {
                return CreateProcess(hDup, commandLine, priorityClass, workingDirectory, showWindow, targetSessionId);
            }
            finally
            {
                NativeMethods.CloseHandle(hDup);
            }
        }
        finally
        {
            if (closeSource) NativeMethods.CloseHandle(source);
        }
    }

    private void EnsurePrivileges()
    {
        if (_privilegesAdjusted) return;
        Log("Enabling privileges...");
        AdjustPrivileges();
        _privilegesAdjusted = true;
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

        // Force a fresh, fully-logged acquisition instead of reusing the token
        // cached at service start — so the entire chain (enable privileges →
        // impersonate winlogon → start TrustedInstaller → grab its thread →
        // duplicate token → revert) streams to the validation Details pane.
        Log("Validation: forcing a fresh TrustedInstaller token acquisition...");
        ReleaseToken();
        _initialized = false;
        Initialize();

        if (_hElevatedToken == IntPtr.Zero)
        {
            Log("Validation: TrustedInstaller token could not be acquired.");
            return false;
        }

        Log("Validating freshly-acquired TrustedInstaller token...");
        LogTokenPrivileges(_hElevatedToken);

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

        bool userIsTI =
            string.Equals(sidString, TrustedInstallerSid, StringComparison.OrdinalIgnoreCase)
            || account.EndsWith("TrustedInstaller", StringComparison.OrdinalIgnoreCase);

        // The Windows Modules Installer (TrustedInstaller) service runs as
        // LocalSystem, so a token stolen from it has user = SYSTEM with the
        // TrustedInstaller SID carried as a GROUP. That group is what grants
        // TrustedInstaller-level access, so it is the real success criterion.
        bool groupHasTI = TokenHasGroup(_hElevatedToken, TrustedInstallerSid);
        Log($"TrustedInstaller group present in token: {groupHasTI}");

        bool isTrustedInstaller = userIsTI || groupHasTI;

        if (userIsTI)
            Log("Validation OK: token user is NT SERVICE\\TrustedInstaller.");
        else if (groupHasTI)
            Log("Validation OK: token runs as SYSTEM and carries the NT SERVICE\\TrustedInstaller group (TrustedInstaller-level access).");
        else
            Log($"Validation FAILED: token has neither the TrustedInstaller user nor group (resolved {account}).");

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

    // True if the token carries the given SID (string form) as a group. Used to
    // detect TrustedInstaller-level access on a SYSTEM-user token.
    private unsafe bool TokenHasGroup(IntPtr hToken, string sidString)
    {
        NativeMethods.GetTokenInformation(
            hToken, NativeMethods.TOKEN_INFORMATION_CLASS.TokenGroups, null, 0, out uint len);
        if (len == 0) return false;

        byte* buf = stackalloc byte[(int)len];
        if (!NativeMethods.GetTokenInformation(
                hToken, NativeMethods.TOKEN_INFORMATION_CLASS.TokenGroups, buf, len, out _))
            return false;

        // TOKEN_GROUPS = DWORD GroupCount; SID_AND_ATTRIBUTES Groups[]. The array
        // is pointer-aligned, so it starts at IntPtr.Size (x64) after the count.
        uint count = *(uint*)buf;
        var groups = (NativeMethods.SID_AND_ATTRIBUTES*)(buf + IntPtr.Size);

        for (uint i = 0; i < count; i++)
        {
            if (NativeMethods.ConvertSidToStringSidW(groups[i].Sid, out IntPtr pStr) && pStr != IntPtr.Zero)
            {
                string s = Marshal.PtrToStringUni(pStr) ?? string.Empty;
                NativeMethods.LocalFree(pStr);
                if (string.Equals(s, sidString, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    // Logs the full privilege set carried by the stolen token, each marked
    // (on)/(off), so validation shows exactly what the acquired token can do.
    private unsafe void LogTokenPrivileges(IntPtr hToken)
    {
        NativeMethods.GetTokenInformation(
            hToken, NativeMethods.TOKEN_INFORMATION_CLASS.TokenPrivileges, null, 0, out uint len);
        if (len == 0) { Log("Token privileges: (unavailable)"); return; }

        byte* buf = stackalloc byte[(int)len];
        if (!NativeMethods.GetTokenInformation(
                hToken, NativeMethods.TOKEN_INFORMATION_CLASS.TokenPrivileges, buf, len, out _))
        {
            Log($"Token privileges: query failed. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
            return;
        }

        // TOKEN_PRIVILEGES = DWORD PrivilegeCount; LUID_AND_ATTRIBUTES Privileges[].
        uint count = *(uint*)buf;
        var laa = (NativeMethods.LUID_AND_ATTRIBUTES*)(buf + sizeof(uint));

        var sb = new System.Text.StringBuilder();
        for (uint i = 0; i < count; i++)
        {
            bool enabled = (laa[i].Attributes & NativeMethods.SE_PRIVILEGE_ENABLED) != 0;
            sb.Append(LookupPrivName(laa[i].Luid)).Append(enabled ? "(on) " : "(off) ");
        }
        Log($"Token privileges [{count}]: {sb.ToString().TrimEnd()}");
    }

    private unsafe string LookupPrivName(NativeMethods.LUID luid)
    {
        char* nameBuf = stackalloc char[256];
        uint  cch     = 256;
        NativeMethods.LUID local = luid;
        if (NativeMethods.LookupPrivilegeNameW(null, &local, nameBuf, ref cch))
            return new string(nameBuf, 0, (int)cch);
        return $"(luid {luid.HighPart:X}:{luid.LowPart:X})";
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
                NativeMethods.SE_TCB_NAME,                  // SetTokenInformation(TokenSessionId)
                NativeMethods.SE_ASSIGNPRIMARYTOKEN_NAME,   // CreateProcessAsUser
                NativeMethods.SE_INCREASE_QUOTA_NAME,       // CreateProcessAsUser
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
        long deadline = Environment.TickCount64 + 30_000;

        while (NativeMethods.QueryServiceStatusEx(
            hSvc, NativeMethods.SC_STATUS_PROCESS_INFO,
            out NativeMethods.SERVICE_STATUS_PROCESS st, svcSize, out _))
        {
            if (Environment.TickCount64 > deadline)
            {
                Log("Timed out waiting for TrustedInstaller service to run.");
                return 0;
            }

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
                    uint waitMs = Math.Clamp(st.dwWaitHint, 250u, 5_000u);
                    Log($"Service pending, waiting {waitMs}ms...");
                    NativeMethods.Sleep(waitMs);
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

    // Returns the PID of the created process, or 0 on failure.
    private unsafe uint CreateProcess(IntPtr hToken, string commandLine, uint priorityClass,
        string? workingDirectory = null,
        int showWindow = NativeMethods.SW_SHOWNORMAL,
        uint? targetSessionId = null)
    {
        Log("Creating process...");

        // Log the session topology: the service's own session (expected 0) and
        // the client/console session we will launch the process into.
        NativeMethods.ProcessIdToSessionId(NativeMethods.GetCurrentProcessId(), out uint svcSession);

        // Remap the token to the requested interactive session so the launched process
        // appears on the interactive desktop instead of the invisible Session 0.
        uint sessionId = targetSessionId ?? NativeMethods.WTSGetActiveConsoleSessionId();
        string sessionSource = targetSessionId.HasValue ? "requesting client" : "active console";
        Log($"Launcher (service) session={svcSession}; target session={sessionId} ({sessionSource}).");
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

        // Non-executable targets can't be launched by CreateProcess directly, so
        // run them via their host (e.g. an .msc snap-in via mmc.exe). The host is
        // resolved from PATH by passing a null lpApplicationName below.
        string consoleProbe = string.IsNullOrEmpty(args) ? commandLine : app;
        string? host = HostExe(app);
        if (host is not null)
        {
            commandLine  = BuildHostCommand(host, app, args);
            args         = string.Empty;   // take the PATH-resolved (null app) branch
            consoleProbe = host;           // console state follows the host
            Log($"Non-executable target — launching via {host}: {commandLine}");
        }

        // Console-subsystem programs (cmd, powershell) launched from a service
        // get no usable window unless we allocate a fresh console. GUI apps
        // (regedit, notepad, mmc) must NOT get one, or an empty console flashes up.
        uint creationFlags = NativeMethods.CREATE_UNICODE_ENVIRONMENT | priorityClass;
        if (IsConsoleSubsystem(consoleProbe))
        {
            creationFlags |= NativeMethods.CREATE_NEW_CONSOLE;
            Log("Console application detected — allocating an interactive console window.");
        }

        // Working directory: empty = inherit; expand env vars (e.g. %USERPROFILE%).
        string? workDir = string.IsNullOrWhiteSpace(workingDirectory)
            ? null
            : (workingDirectory.Contains('%') ? ExpandEnvVars(workingDirectory) : workingDirectory);
        if (workDir is not null) Log($"Working directory: {workDir}");
        Log($"Window state (SW_*): {showWindow}");

        fixed (char* pDesktop = "WinSta0\\Default")
        {
            var si = new NativeMethods.STARTUPINFOW
            {
                cb          = (uint)sizeof(NativeMethods.STARTUPINFOW),
                lpDesktop   = (IntPtr)pDesktop,
                dwFlags     = NativeMethods.STARTF_USESHOWWINDOW,
                wShowWindow = (ushort)showWindow,
            };

            // CreateProcessAsUser (not CreateProcessWithTokenW): the latter places
            // the child in the service's Session 0 regardless of the token's
            // session id, so its window is invisible on the user's desktop.
            // CreateProcessAsUser honours the token's (remapped) session, putting
            // the process on the interactive desktop.
            NativeMethods.PROCESS_INFORMATION pi;
            bool result;
            if (string.IsNullOrEmpty(args))
            {
                if (commandLine.Contains('%'))
                    commandLine = ExpandEnvVars(commandLine);

                result = NativeMethods.CreateProcessAsUserW(
                    hToken, null, commandLine,
                    IntPtr.Zero, IntPtr.Zero, false,
                    creationFlags,
                    IntPtr.Zero, workDir, &si, out pi);
            }
            else
            {
                Log($"Args detected — app={app}  args={args}");
                if (app.Contains('%')) app = ExpandEnvVars(app);

                result = NativeMethods.CreateProcessAsUserW(
                    hToken, app, commandLine,
                    IntPtr.Zero, IntPtr.Zero, false,
                    creationFlags,
                    IntPtr.Zero, workDir, &si, out pi);
            }

            if (!result)
            {
                Log($"CreateProcessAsUser failed. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                return 0;
            }

            NativeMethods.CloseHandle(pi.hProcess);
            NativeMethods.CloseHandle(pi.hThread);
            Log($"Process created: PID={pi.dwProcessId} session={sessionId} desktop=WinSta0\\Default.");
            return pi.dwProcessId;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private unsafe uint FindProcessByName(string name)
    {
        IntPtr hSnap = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (hSnap == IntPtr.Zero || hSnap == NativeMethods.INVALID_HANDLE_VALUE) return 0;
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
        if (hSnap == IntPtr.Zero || hSnap == NativeMethods.INVALID_HANDLE_VALUE) return 0;
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

    // The host executable for a non-executable target, or null if the target is
    // itself runnable (.exe/.com). Lets saved .msc/.cpl/.bat/.ps1 entries launch.
    private static string? HostExe(string app)
    {
        string ext = System.IO.Path.GetExtension(app.Trim().Trim('"')).ToLowerInvariant();
        return ext switch
        {
            ".msc"           => "mmc.exe",
            ".cpl"           => "control.exe",
            ".bat" or ".cmd" => "cmd.exe",
            ".ps1"           => "powershell.exe",
            _                => null,
        };
    }

    private static string BuildHostCommand(string host, string app, string args)
    {
        string quoted = $"\"{app.Trim().Trim('"')}\"";
        string head = host switch
        {
            "cmd.exe"        => $"cmd.exe /c {quoted}",
            "powershell.exe" => $"powershell.exe -ExecutionPolicy Bypass -File {quoted}",
            "control.exe"    => $"control.exe {quoted}",
            _                => $"mmc.exe {quoted}",
        };
        return string.IsNullOrEmpty(args) ? head : $"{head} {args}";
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
