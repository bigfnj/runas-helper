using System;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace RunAsHelper.Service.Core;

/// <summary>
/// Acquires a TrustedInstaller token and launches processes under it.
/// Designed to run inside a Windows service (Session 0 / LocalSystem).
/// Differences from the tray version:
///   — DuplicateTokenEx uses TokenPrimary (required by CreateProcessAsUserW).
///   — SetTokenInformation(TokenSessionId) maps the launch to the requesting
///     client session so the process appears on the user's interactive desktop.
///   — SeTcbPrivilege is enabled to allow the session-ID change.
/// Thread-safety: Initialize() is idempotent and lock-protected. LaunchElevated()
/// is safe for concurrent calls after init. ValidateToken() resets init state so
/// it should not run concurrently with LaunchElevated(); callers enforce this.
/// </summary>
internal sealed class ElevationLauncher
{
    private volatile bool   _initialized;
    private volatile bool   _privilegesAdjusted;
    private volatile IntPtr _hElevatedToken = IntPtr.Zero;
    private          IntPtr _hNtDll   = IntPtr.Zero;

    // Guards the one-time initialization path (idempotent after first success).
    private readonly object _initLock = new();

    // ── Public API ───────────────────────────────────────────────────────

    public bool IsReady => _hElevatedToken != IntPtr.Zero;

    /// <summary>
    /// Runs the privilege chain and caches the TrustedInstaller token.
    /// Idempotent and thread-safe — lock-protected so only one thread runs
    /// the acquisition chain even under concurrent launch requests.
    /// </summary>
    public void Initialize(Action<string>? log = null)
    {
        lock (_initLock)
        {
            EnsurePrivileges(log);
            if (!_initialized)
            {
                log?.Invoke("Impersonating system...");
                if (!ImpersonateSystem(log)) { log?.Invoke("Failed to impersonate system."); return; }
                _initialized = true;
            }

            try
            {
                if (_hElevatedToken == IntPtr.Zero)
                    StartAndAcquireToken(log);
            }
            finally
            {
                // Drop the impersonation mask before returning to the thread pool.
                // The cached token handle in _hElevatedToken remains valid.
                if (!NativeMethods.RevertToSelf())
                    log?.Invoke($"Warning: RevertToSelf failed. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                else
                    log?.Invoke("Worker thread reverted to self (impersonation dropped after token clone).");
            }
        }
    }

    // Returns (pid, hProcess, stdout). hProcess and stdout are non-zero/non-null
    // only when captureOutput=true; the caller owns both and must close/dispose them.
    // Safe to call concurrently after Initialize() has succeeded at least once.
    public (uint Pid, IntPtr hProcess, System.IO.Stream? Stdout) LaunchElevated(
        string commandLine,
        uint priorityClass = NativeMethods.NORMAL_PRIORITY_CLASS,
        string? workingDirectory = null,
        int showWindow = NativeMethods.SW_SHOWNORMAL,
        string account = "ti",
        uint? targetSessionId = null,
        bool captureOutput = false,
        Action<string>? log = null)
    {
        bool asSystem = string.Equals(account, "system", StringComparison.OrdinalIgnoreCase);

        // Pick the source token: the LocalSystem (service) token for account=system
        // — a pure SYSTEM token with no TrustedInstaller group — or the stolen
        // TrustedInstaller token (SYSTEM + TI group) otherwise.
        IntPtr source;
        bool   closeSource = false;
        if (asSystem)
        {
            EnsurePrivileges(log);
            if (!NativeMethods.OpenProcessToken(
                    NativeMethods.GetCurrentProcess(),
                    NativeMethods.TOKEN_DUPLICATE | NativeMethods.TOKEN_QUERY, out source))
            {
                log?.Invoke($"Failed to open LocalSystem token. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                return (0, IntPtr.Zero, null);
            }
            closeSource = true;
            log?.Invoke("Account=system — launching with the LocalSystem token (no TrustedInstaller group).");
        }
        else
        {
            Initialize(log);
            if (_hElevatedToken == IntPtr.Zero)
            {
                log?.Invoke("Failed to acquire elevated token");
                return (0, IntPtr.Zero, null);
            }
            source = _hElevatedToken;
            log?.Invoke("Account=trustedinstaller — launching with the TrustedInstaller token.");
        }

        try
        {
            log?.Invoke("Duplicating token...");
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
                    log?.Invoke($"LaunchElevated::Failed to duplicate token, " +
                        $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                    return (0, IntPtr.Zero, null);
                }
            }

            try
            {
                return CreateProcess(hDup, commandLine, priorityClass, workingDirectory, showWindow, targetSessionId, captureOutput, log);
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

    private void EnsurePrivileges(Action<string>? log = null)
    {
        if (_privilegesAdjusted) return;
        log?.Invoke("Enabling privileges...");
        AdjustPrivileges(log);
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
    /// Note: resets cached token state — do not call concurrently with LaunchElevated.
    /// </summary>
    public unsafe bool ValidateToken(out string account, Action<string>? log = null)
    {
        account = string.Empty;

        // Force a fresh, fully-logged acquisition instead of reusing the token
        // cached at service start — so the entire chain (enable privileges →
        // impersonate winlogon → start TrustedInstaller → grab its thread →
        // duplicate token → revert) streams to the validation Details pane.
        log?.Invoke("Validation: forcing a fresh TrustedInstaller token acquisition...");
        lock (_initLock)
        {
            ReleaseToken();
            _initialized = false;
            Initialize(log);
        }

        if (_hElevatedToken == IntPtr.Zero)
        {
            log?.Invoke("Validation: TrustedInstaller token could not be acquired.");
            return false;
        }

        log?.Invoke("Validating freshly-acquired TrustedInstaller token...");
        LogTokenPrivileges(_hElevatedToken, log);

        // First call sizes the buffer (returns false + ERROR_INSUFFICIENT_BUFFER).
        NativeMethods.GetTokenInformation(
            _hElevatedToken, NativeMethods.TOKEN_INFORMATION_CLASS.TokenUser,
            null, 0, out uint len);
        if (len == 0)
        {
            log?.Invoke($"Validation: GetTokenInformation(size) failed. " +
                $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
            return false;
        }

        byte* buf = stackalloc byte[(int)len];
        if (!NativeMethods.GetTokenInformation(
                _hElevatedToken, NativeMethods.TOKEN_INFORMATION_CLASS.TokenUser,
                buf, len, out _))
        {
            log?.Invoke($"Validation: GetTokenInformation failed. " +
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
        log?.Invoke($"Token user: {account} (SID {sidString})");

        bool userIsTI =
            string.Equals(sidString, TrustedInstallerSid, StringComparison.OrdinalIgnoreCase)
            || account.EndsWith("TrustedInstaller", StringComparison.OrdinalIgnoreCase);

        // The Windows Modules Installer (TrustedInstaller) service runs as
        // LocalSystem, so a token stolen from it has user = SYSTEM with the
        // TrustedInstaller SID carried as a GROUP. That group is what grants
        // TrustedInstaller-level access, so it is the real success criterion.
        bool groupHasTI = TokenHasGroup(_hElevatedToken, TrustedInstallerSid);
        log?.Invoke($"TrustedInstaller group present in token: {groupHasTI}");

        bool isTrustedInstaller = userIsTI || groupHasTI;

        if (userIsTI)
            log?.Invoke("Validation OK: token user is NT SERVICE\\TrustedInstaller.");
        else if (groupHasTI)
            log?.Invoke("Validation OK: token runs as SYSTEM and carries the NT SERVICE\\TrustedInstaller group (TrustedInstaller-level access).");
        else
            log?.Invoke($"Validation FAILED: token has neither the TrustedInstaller user nor group (resolved {account}).");

        // Belt and braces: make sure no impersonation mask remains on this thread
        // before it returns to the pool. (Initialize already reverts; repeat here
        // so a future refactor of Initialize cannot silently leak impersonation.)
        if (!NativeMethods.RevertToSelf())
            log?.Invoke($"Warning: RevertToSelf after validation failed. " +
                $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");

        return isTrustedInstaller;
    }

    // Well-known SID of NT SERVICE\TrustedInstaller.
    private const string TrustedInstallerSid =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    // Well-known SID for NT AUTHORITY\SYSTEM (LocalSystem).
    private const string SystemSid = "S-1-5-18";

    /// <summary>
    /// Validates that the service's own LocalSystem token (the SYSTEM account path
    /// used when account="system") is accessible and well-formed. Mirrors the
    /// structure of <see cref="ValidateToken"/> for symmetry in the validation UI.
    /// </summary>
    public unsafe bool ValidateSystemToken(out string account, Action<string>? log = null)
    {
        account = string.Empty;

        log?.Invoke("Validation: acquiring LocalSystem (SYSTEM) token...");
        EnsurePrivileges(log);

        if (!NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                NativeMethods.TOKEN_DUPLICATE | NativeMethods.TOKEN_QUERY, out IntPtr hToken))
        {
            log?.Invoke($"Validation: OpenProcessToken failed. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
            return false;
        }

        try
        {
            log?.Invoke("LocalSystem token opened.");
            LogTokenPrivileges(hToken, log);

            NativeMethods.GetTokenInformation(
                hToken, NativeMethods.TOKEN_INFORMATION_CLASS.TokenUser,
                null, 0, out uint len);
            if (len == 0)
            {
                log?.Invoke($"Validation: GetTokenInformation(size) failed. " +
                    $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                return false;
            }

            byte* buf = stackalloc byte[(int)len];
            if (!NativeMethods.GetTokenInformation(
                    hToken, NativeMethods.TOKEN_INFORMATION_CLASS.TokenUser,
                    buf, len, out _))
            {
                log?.Invoke($"Validation: GetTokenInformation failed. " +
                    $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                return false;
            }

            IntPtr pSid = *(IntPtr*)buf;

            string sidString = "(unknown)";
            if (NativeMethods.ConvertSidToStringSidW(pSid, out IntPtr pStr) && pStr != IntPtr.Zero)
            {
                sidString = Marshal.PtrToStringUni(pStr) ?? sidString;
                NativeMethods.LocalFree(pStr);
            }

            account = LookupSid(pSid);
            log?.Invoke($"Token user: {account} (SID {sidString})");

            bool isSystem =
                string.Equals(sidString, SystemSid, StringComparison.OrdinalIgnoreCase)
                || account.EndsWith("SYSTEM", StringComparison.OrdinalIgnoreCase);

            if (isSystem)
                log?.Invoke("Validation OK: token is NT AUTHORITY\\SYSTEM.");
            else
                log?.Invoke($"Validation FAILED: unexpected account ({account}, SID {sidString}).");

            log?.Invoke("SYSTEM token released.");
            return isSystem;
        }
        finally
        {
            NativeMethods.CloseHandle(hToken);
        }
    }

    private unsafe string LookupSid(IntPtr pSid, Action<string>? log = null)
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
    private unsafe void LogTokenPrivileges(IntPtr hToken, Action<string>? log = null)
    {
        NativeMethods.GetTokenInformation(
            hToken, NativeMethods.TOKEN_INFORMATION_CLASS.TokenPrivileges, null, 0, out uint len);
        if (len == 0) { log?.Invoke("Token privileges: (unavailable)"); return; }

        byte* buf = stackalloc byte[(int)len];
        if (!NativeMethods.GetTokenInformation(
                hToken, NativeMethods.TOKEN_INFORMATION_CLASS.TokenPrivileges, buf, len, out _))
        {
            log?.Invoke($"Token privileges: query failed. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
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
        log?.Invoke($"Token privileges [{count}]: {sb.ToString().TrimEnd()}");
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

    private void AdjustPrivileges(Action<string>? log = null)
    {
        if (!NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY,
                out IntPtr hToken))
        {
            log?.Invoke("AdjustPrivileges::Failed to open process token.");
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
                if (SetPrivilege(hToken, priv, true, log))
                    log?.Invoke($"AdjustPrivileges::Enabled {priv}.");
                else
                    log?.Invoke($"AdjustPrivileges::Failed to enable {priv}.");
            }
        }
        finally { NativeMethods.CloseHandle(hToken); }
    }

    private unsafe bool SetPrivilege(IntPtr hToken, string privilege, bool enable, Action<string>? log = null)
    {
        NativeMethods.LUID luid;
        if (!NativeMethods.LookupPrivilegeValueW(null, privilege, &luid))
        {
            log?.Invoke($"SetPrivilege::LookupPrivilegeValue failed for {privilege}. " +
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
        if (err != 0) { log?.Invoke($"SetPrivilege::Error={GetErrorName(err)}"); return false; }
        return true;
    }

    // ── System impersonation ─────────────────────────────────────────────

    private bool ImpersonateSystem(Action<string>? log = null)
    {
        uint pidWinLogon = FindProcessByName("winlogon.exe");
        if (pidWinLogon == 0) { log?.Invoke("Failed to find winlogon pid."); return false; }

        log?.Invoke("Got winlogon pid, opening process...");
        IntPtr hWinLogon = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_DUP_HANDLE | NativeMethods.PROCESS_QUERY_INFORMATION,
            false, pidWinLogon);
        if (hWinLogon == IntPtr.Zero)
        {
            log?.Invoke($"Failed to open winlogon. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
            return false;
        }
        try
        {
            if (!NativeMethods.OpenProcessToken(
                    hWinLogon,
                    NativeMethods.TOKEN_QUERY | NativeMethods.TOKEN_DUPLICATE,
                    out IntPtr hSysTkn))
            {
                log?.Invoke($"Failed to open winlogon token. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                return false;
            }
            try
            {
                bool ok = NativeMethods.ImpersonateLoggedOnUser(hSysTkn);
                if (ok) log?.Invoke("Successfully impersonated system!");
                else    log?.Invoke($"Failed to impersonate. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                return ok;
            }
            finally { NativeMethods.CloseHandle(hSysTkn); }
        }
        finally { NativeMethods.CloseHandle(hWinLogon); }
    }

    // ── Elevated token acquisition ─────────────────────────────────────

    private void StartAndAcquireToken(Action<string>? log = null)
    {
        IntPtr hSCM = NativeMethods.OpenSCManagerW(null, null, NativeMethods.SC_MANAGER_ALL_ACCESS);
        if (hSCM == IntPtr.Zero)
        {
            log?.Invoke($"Failed to open SCManager. error={GetErrorName((uint)Marshal.GetLastWin32Error())}");
            return;
        }
        try
        {
            IntPtr hSvc = NativeMethods.OpenServiceW(hSCM, "TrustedInstaller",
                NativeMethods.SERVICE_START | NativeMethods.SERVICE_QUERY_STATUS);
            if (hSvc == IntPtr.Zero)
            {
                log?.Invoke($"Failed to open TrustedInstaller service. error={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                return;
            }
            try
            {
                uint pid = WaitForServiceRunning(hSvc, log);
                if (pid != 0) AcquireTokenFromProcess(pid, log);
            }
            finally { NativeMethods.CloseHandle(hSvc); }
        }
        finally { NativeMethods.CloseHandle(hSCM); }
    }

    private unsafe uint WaitForServiceRunning(IntPtr hSvc, Action<string>? log = null)
    {
        log?.Invoke("Waiting for TrustedInstaller service...");
        uint svcSize = (uint)sizeof(NativeMethods.SERVICE_STATUS_PROCESS);
        long deadline = Environment.TickCount64 + 30_000;

        while (NativeMethods.QueryServiceStatusEx(
            hSvc, NativeMethods.SC_STATUS_PROCESS_INFO,
            out NativeMethods.SERVICE_STATUS_PROCESS st, svcSize, out _))
        {
            if (Environment.TickCount64 > deadline)
            {
                log?.Invoke("Timed out waiting for TrustedInstaller service to run.");
                return 0;
            }

            switch (st.dwCurrentState)
            {
                case NativeMethods.ServiceState.Stopped:
                    log?.Invoke("Service stopped, starting...");
                    if (!NativeMethods.StartServiceW(hSvc, 0, IntPtr.Zero))
                    {
                        uint err = (uint)Marshal.GetLastWin32Error();
                        if (err != NativeMethods.ERROR_SERVICE_ALREADY_RUNNING)
                        {
                            log?.Invoke($"Error starting TrustedInstaller. error={GetErrorName(err)}");
                            return 0;
                        }
                    }
                    break;

                case NativeMethods.ServiceState.StartPending:
                case NativeMethods.ServiceState.StopPending:
                    uint waitMs = Math.Clamp(st.dwWaitHint, 250u, 5_000u);
                    log?.Invoke($"Service pending, waiting {waitMs}ms...");
                    NativeMethods.Sleep(waitMs);
                    break;

                case NativeMethods.ServiceState.Running:
                    log?.Invoke($"TrustedInstaller running, pid={st.dwProcessId}.");
                    return st.dwProcessId;
            }
        }
        return 0;
    }

    private unsafe void AcquireTokenFromProcess(uint pid, Action<string>? log = null)
    {
        uint tid = GetFirstThreadId(pid);
        log?.Invoke($"Elevated thread id={tid}");
        if (tid == 0) { log?.Invoke("Failed to get elevated thread id."); return; }

        IntPtr hThread = NativeMethods.OpenThread(
            NativeMethods.THREAD_DIRECT_IMPERSONATION, false, tid);
        if (hThread == IntPtr.Zero)
        {
            log?.Invoke($"Failed to open elevated thread. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
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
                log?.Invoke($"NtImpersonateThread failed, NTSTATUS={GetNtStatusName(status)}");
                return;
            }

            log?.Invoke("NtImpersonateThread STATUS_SUCCESS. Opening thread token...");
            // Open into a local first: a volatile field cannot be passed as an
            // 'out' argument without losing its volatile semantics (CS0420).
            if (NativeMethods.OpenThreadToken(
                    NativeMethods.GetCurrentThread(),
                    NativeMethods.TOKEN_ALL_ACCESS,
                    false, out IntPtr hToken))
            {
                _hElevatedToken = hToken;
                log?.Invoke("Token acquired and cached.");
            }
            else
                log?.Invoke($"OpenThreadToken failed. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
        }
        finally { NativeMethods.CloseHandle(hThread); }
    }

    // ── Process creation (session-aware for service context) ─────────────

    // Returns (pid, hProcess, stdout).
    // When captureOutput=false (default), hProcess=Zero and stdout=null — the
    // process is fire-and-forget.  When captureOutput=true the caller receives
    // an open process handle (to wait on) and a stream for the child's merged
    // stdout+stderr; the caller is responsible for closing both.
    private unsafe (uint pid, IntPtr hProcess, System.IO.Stream? stdout) CreateProcess(
        IntPtr hToken, string commandLine, uint priorityClass,
        string? workingDirectory = null,
        int showWindow = NativeMethods.SW_SHOWNORMAL,
        uint? targetSessionId = null,
        bool captureOutput = false,
        Action<string>? log = null)
    {
        log?.Invoke("Creating process...");

        // Log the session topology: the service's own session (expected 0) and
        // the client/console session we will launch the process into.
        NativeMethods.ProcessIdToSessionId(NativeMethods.GetCurrentProcessId(), out uint svcSession);

        // Remap the token to the requested interactive session so the launched process
        // appears on the interactive desktop instead of the invisible Session 0.
        uint sessionId = targetSessionId ?? NativeMethods.WTSGetActiveConsoleSessionId();
        string sessionSource = targetSessionId.HasValue ? "requesting client" : "active console";
        log?.Invoke($"Launcher (service) session={svcSession}; target session={sessionId} ({sessionSource}).");
        if (sessionId != uint.MaxValue)
        {
            if (!NativeMethods.SetTokenInformation(
                    hToken,
                    NativeMethods.TOKEN_INFORMATION_CLASS.TokenSessionId,
                    &sessionId,
                    sizeof(uint)))
            {
                log?.Invoke($"Warning: SetTokenInformation(TokenSessionId) failed. " +
                    $"lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
            }
            else
            {
                log?.Invoke($"Token session remapped to session {sessionId}.");
            }
        }

        var (app, args) = ParseCommandLine(commandLine);

        // Non-executable targets can't be launched by CreateProcess directly, so
        // run them via their host (e.g. an .msc snap-in via mmc.exe). The host is
        // resolved from PATH by passing a null lpApplicationName below.
        string consoleProbe = string.IsNullOrEmpty(args) ? commandLine : app;
        string? host = HostExe(app);
        if (host == ShellOpenHost)
        {
            // Document: resolve the registered handler and launch it directly.
            string? docCommand = ResolveDocumentCommand(app.Trim().Trim('"'), log);
            if (docCommand is null)
            {
                log?.Invoke("Cannot open this file — no registered handler. Point at a program instead.");
                return (0, IntPtr.Zero, null);
            }
            commandLine  = string.IsNullOrEmpty(args) ? docCommand : $"{docCommand} {args}";
            args         = string.Empty;   // take the PATH-resolved (null app) branch
            var (handlerExe, _) = ParseCommandLine(commandLine);
            consoleProbe = handlerExe;     // console state follows the resolved handler
        }
        else if (host is not null)
        {
            commandLine  = BuildHostCommand(host, app, args);
            args         = string.Empty;   // take the PATH-resolved (null app) branch
            consoleProbe = host;           // console state follows the host
            log?.Invoke($"Non-executable target — launching via {host}: {commandLine}");
        }

        // Console-subsystem programs (cmd, powershell) launched from a service
        // get no usable window unless we allocate a fresh console. GUI apps
        // (regedit, notepad, mmc) must NOT get one, or an empty console flashes up.
        // In capture mode, no console window is wanted — stdout goes to the pipe.
        uint creationFlags = NativeMethods.CREATE_UNICODE_ENVIRONMENT | priorityClass;
        if (captureOutput)
        {
            creationFlags |= NativeMethods.CREATE_NO_WINDOW;
            log?.Invoke("Output capture mode — no console window; stdout/stderr piped back to caller.");
        }
        else if (IsConsoleSubsystem(consoleProbe))
        {
            creationFlags |= NativeMethods.CREATE_NEW_CONSOLE;
            log?.Invoke("Console application detected — allocating an interactive console window.");
        }

        // Working directory: empty = inherit; expand env vars (e.g. %USERPROFILE%).
        string? workDir = string.IsNullOrWhiteSpace(workingDirectory)
            ? null
            : (workingDirectory.Contains('%') ? ExpandEnvVars(workingDirectory) : workingDirectory);
        if (workDir is not null) log?.Invoke($"Working directory: {workDir}");
        log?.Invoke($"Window state (SW_*): {showWindow}");

        // Set up the stdout/stderr capture pipe when capture is requested.
        // The write end (hWritePipe) is inheritable and passed to the child via
        // STARTUPINFOEX; the read end stays in the service as an async-capable stream.
        // PROC_THREAD_ATTRIBUTE_HANDLE_LIST restricts inheritance to only hWritePipe
        // so no other service handles leak into the elevated child process.
        NamedPipeServerStream? captureServer = null;
        IntPtr hWritePipe = IntPtr.Zero;

        if (captureOutput)
        {
            if (!TryCreateCapturePipe(out captureServer, out hWritePipe, log))
                captureOutput = false;
        }

        fixed (char* pDesktop = "WinSta0\\Default")
        {
            NativeMethods.PROCESS_INFORMATION pi = default;
            bool callResult;

            if (captureOutput)
            {
                // Build a PROC_THREAD_ATTRIBUTE_LIST that names only hWritePipe as
                // the handle to inherit — guards against leaking every other open
                // service handle into the TrustedInstaller/SYSTEM child process.
                nuint attrSize = 0;
                NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, &attrSize);
                IntPtr attrList = Marshal.AllocHGlobal((int)attrSize);
                try
                {
                    NativeMethods.InitializeProcThreadAttributeList(attrList, 1, 0, &attrSize);
                    try
                    {
                        IntPtr toInherit = hWritePipe;
                        NativeMethods.UpdateProcThreadAttribute(
                            attrList, 0,
                            NativeMethods.PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                            &toInherit, (nuint)IntPtr.Size, null, null);

                        var siex = new NativeMethods.STARTUPINFOEXW
                        {
                            StartupInfo = new NativeMethods.STARTUPINFOW
                            {
                                cb          = (uint)sizeof(NativeMethods.STARTUPINFOEXW),
                                lpDesktop   = (IntPtr)pDesktop,
                                dwFlags     = NativeMethods.STARTF_USESHOWWINDOW | NativeMethods.STARTF_USESTDHANDLES,
                                wShowWindow = (ushort)showWindow,
                                hStdInput   = IntPtr.Zero,
                                hStdOutput  = hWritePipe,
                                hStdError   = hWritePipe,
                            },
                            lpAttributeList = attrList,
                        };

                        uint captureFlags = creationFlags | NativeMethods.EXTENDED_STARTUPINFO_PRESENT;

                        if (string.IsNullOrEmpty(args))
                        {
                            if (commandLine.Contains('%')) commandLine = ExpandEnvVars(commandLine);
                            callResult = NativeMethods.CreateProcessAsUserExW(
                                hToken, null, commandLine,
                                IntPtr.Zero, IntPtr.Zero, true,
                                captureFlags,
                                IntPtr.Zero, workDir, &siex, out pi);
                        }
                        else
                        {
                            if (app.Contains('%')) app = ExpandEnvVars(app);
                            string launchApp = ResolveExecutable(app) ?? app;
                            log?.Invoke($"Args detected — app={launchApp}  args={args}");
                            callResult = NativeMethods.CreateProcessAsUserExW(
                                hToken, launchApp, commandLine,
                                IntPtr.Zero, IntPtr.Zero, true,
                                captureFlags,
                                IntPtr.Zero, workDir, &siex, out pi);
                        }
                    }
                    finally { NativeMethods.DeleteProcThreadAttributeList(attrList); }
                }
                finally { Marshal.FreeHGlobal(attrList); }
            }
            else
            {
                // Standard (fire-and-forget) path — unchanged from the original.
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
                if (string.IsNullOrEmpty(args))
                {
                    if (commandLine.Contains('%'))
                        commandLine = ExpandEnvVars(commandLine);

                    callResult = NativeMethods.CreateProcessAsUserW(
                        hToken, null, commandLine,
                        IntPtr.Zero, IntPtr.Zero, false,
                        creationFlags,
                        IntPtr.Zero, workDir, &si, out pi);
                }
                else
                {
                    if (app.Contains('%')) app = ExpandEnvVars(app);
                    // CreateProcessAsUser does NOT search PATH for a non-null
                    // lpApplicationName — a bare name resolves only against the
                    // service's working directory (C:\Windows\System32). cmd.exe lives
                    // there, but powershell.exe (System32\WindowsPowerShell\v1.0) and
                    // most other tools do not, so they failed with FILE_NOT_FOUND once
                    // arguments were present. Resolve to a full path via PATH first.
                    string launchApp = ResolveExecutable(app) ?? app;
                    log?.Invoke($"Args detected — app={launchApp}  args={args}");

                    callResult = NativeMethods.CreateProcessAsUserW(
                        hToken, launchApp, commandLine,
                        IntPtr.Zero, IntPtr.Zero, false,
                        creationFlags,
                        IntPtr.Zero, workDir, &si, out pi);
                }
            }

            if (!callResult)
            {
                log?.Invoke($"CreateProcessAsUser failed. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())}");
                if (hWritePipe != IntPtr.Zero) NativeMethods.CloseHandle(hWritePipe);
                captureServer?.Dispose();
                return (0, IntPtr.Zero, null);
            }

            NativeMethods.CloseHandle(pi.hThread);

            if (captureOutput && captureServer is not null)
            {
                // Service's copy of the write end is no longer needed — the child
                // holds its own inherited copy. Closing ours here ensures the pipe
                // reaches EOF when the child exits (not when the service decides to).
                NativeMethods.CloseHandle(hWritePipe);

                // The read end is an asynchronous named-pipe stream, so the caller can
                // cancel a pending read (that is what makes /timeout able to release the
                // caller and its launch slot while the child keeps running).
                log?.Invoke($"Process created: PID={pi.dwProcessId} session={sessionId} desktop=WinSta0\\Default (capture mode).");
                return (pi.dwProcessId, pi.hProcess, captureServer);
            }

            NativeMethods.CloseHandle(pi.hProcess);
            log?.Invoke($"Process created: PID={pi.dwProcessId} session={sessionId} desktop=WinSta0\\Default.");
            return (pi.dwProcessId, IntPtr.Zero, null);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the stdout/stderr capture pipe: an async-capable read end kept by the
    /// service, and an inheritable write end handed to the child.
    /// </summary>
    /// <remarks>
    /// This deliberately does <b>not</b> use <c>CreatePipe</c>. Anonymous pipes cannot be
    /// opened for overlapped I/O, so every read on them is a blocking <c>ReadFile</c> that
    /// neither cancellation nor <c>Dispose</c> can interrupt — it only returns at EOF, i.e.
    /// when the child finally exits. That made <c>/timeout:N</c> ineffective: the ceiling
    /// fired and the stream was closed, but the caller (and its launch slot) stayed blocked
    /// for the child's full lifetime. A uniquely-named pipe can be created with
    /// <see cref="PipeOptions.Asynchronous"/>, so reads are genuinely cancellable.
    ///
    /// Security: the child never opens this pipe by name — it receives the write end as an
    /// inherited handle (restricted via PROC_THREAD_ATTRIBUTE_HANDLE_LIST), so the DACL only
    /// has to admit this service. It grants LocalSystem alone, and the name carries a fresh
    /// GUID, so another local process cannot reach the pipe or race us to it.
    /// </remarks>
    private unsafe bool TryCreateCapturePipe(
        out NamedPipeServerStream? server, out IntPtr hWrite, Action<string>? log)
    {
        server = null;
        hWrite = IntPtr.Zero;
        string name = $"RunAsHelper-capture-{Guid.NewGuid():N}";

        try
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

            server = NamedPipeServerStreamAcl.Create(
                name, PipeDirection.In, maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                inBufferSize: 4096, outBufferSize: 4096, security);

            // Start listening before opening the write end, so the connection is
            // established by our own CreateFileW below (no already-connected race).
            var connect = server.WaitForConnectionAsync();

            var sa = new NativeMethods.SECURITY_ATTRIBUTES
            {
                nLength        = (uint)sizeof(NativeMethods.SECURITY_ATTRIBUTES),
                bInheritHandle = 1  // the child inherits this write end
            };
            hWrite = NativeMethods.CreateFileW(
                $@"\\.\pipe\{name}",
                NativeMethods.GENERIC_WRITE,
                0,                                  // no sharing
                &sa,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_ATTRIBUTE_NORMAL, // synchronous: the child writes normally
                IntPtr.Zero);

            if (hWrite == NativeMethods.INVALID_HANDLE_VALUE)
            {
                hWrite = IntPtr.Zero;
                log?.Invoke($"Capture pipe: opening the write end failed. lastErr={GetErrorName((uint)Marshal.GetLastWin32Error())} — falling back to fire-and-forget.");
                server.Dispose();
                server = null;
                return false;
            }

            if (!connect.Wait(5_000))
            {
                log?.Invoke("Capture pipe: the write end did not connect within 5s — falling back to fire-and-forget.");
                NativeMethods.CloseHandle(hWrite);
                hWrite = IntPtr.Zero;
                server.Dispose();
                server = null;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Capture pipe setup failed ({ex.GetType().Name}: {ex.Message}) — falling back to fire-and-forget.");
            if (hWrite != IntPtr.Zero) { NativeMethods.CloseHandle(hWrite); hWrite = IntPtr.Zero; }
            server?.Dispose();
            server = null;
            return false;
        }
    }

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
    // Sentinel host meaning "hand it to the shell to open with whatever is registered
    // for that file type", used for documents that have no host of their own.
    private const string ShellOpenHost = "@shell";

    private static string? HostExe(string app)
    {
        string path = app.Trim().Trim('"');
        string ext  = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".msc"           => "mmc.exe",
            ".cpl"           => "control.exe",
            ".bat" or ".cmd" => "cmd.exe",
            ".ps1"           => "powershell.exe",
            ".reg"           => "regedit.exe",
            // Directly runnable, or a bare name to be resolved on PATH — launch as-is.
            ".exe" or ".com" or "" => null,
            // Anything else is a document: no PE to CreateProcess, so let the shell
            // pick the registered handler. That handler inherits the elevated token,
            // which is the whole point (e.g. editing a TrustedInstaller-owned file).
            _                => ShellOpenHost,
        };
    }

    /// <summary>
    /// Builds a command line that opens <paramref name="path"/> with the handler registered
    /// for its file type, or null if the type has no handler.
    /// </summary>
    /// <remarks>
    /// Deliberately resolves the association here and launches the handler as an ordinary
    /// PE, rather than delegating to the shell with <c>cmd /c start</c>. ShellExecute does
    /// not work from the service's SYSTEM token: it reports no error and simply never
    /// launches anything, which would make a document launch look like a silent no-op. A
    /// directly-launched handler behaves exactly like every other target this tool starts.
    /// </remarks>
    private unsafe string? ResolveDocumentCommand(string path, Action<string>? log)
    {
        string ext = System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return null;

        // The full registered command first: handlers that need extra switches (or that
        // take the file somewhere other than the first argument) only work this way.
        string? command = AssocQuery(NativeMethods.ASSOCSTR_COMMAND, ext);
        // As SYSTEM there is no per-user default, so the association often resolves to the
        // OpenWith chooser. Launching that elevated would put a "how do you want to open
        // this?" dialog on screen running as SYSTEM; report no handler instead. The client
        // resolves documents in the user's own context before it gets this far.
        if (!string.IsNullOrWhiteSpace(command) && command!.Contains("OpenWith.exe", StringComparison.OrdinalIgnoreCase))
            command = null;
        if (!string.IsNullOrWhiteSpace(command))
        {
            string built = SubstituteShellArgs(command!, path);
            log?.Invoke($"Document type {ext} → registered command: {built}");
            return built;
        }

        // Otherwise just the handler executable, with the file as its argument.
        string? exe = AssocQuery(NativeMethods.ASSOCSTR_EXECUTABLE, ext);
        if (!string.IsNullOrWhiteSpace(exe))
        {
            log?.Invoke($"Document type {ext} → handler: {exe}");
            return $"\"{exe}\" \"{path}\"";
        }

        log?.Invoke($"No handler is registered for {ext} files.");
        return null;
    }

    private static unsafe string? AssocQuery(uint what, string ext)
    {
        uint len = 0;
        // First call sizes the buffer; a failure here just means "no association".
        if (NativeMethods.AssocQueryStringW(NativeMethods.ASSOCF_NONE, what, ext, null, null, ref len) < 0
            || len == 0 || len > 4096)
            return null;

        char* buf = stackalloc char[(int)len];
        if (NativeMethods.AssocQueryStringW(NativeMethods.ASSOCF_NONE, what, ext, null, buf, ref len) != 0)
            return null;

        string s = new string(buf).TrimEnd('\0').Trim();
        return s.Length == 0 ? null : s;
    }

    // Fills in a registered command's parameters: %1/%L (and the quoted forms) become the
    // file, and the remaining shell placeholders are dropped rather than passed through
    // literally. If the command names no file parameter at all, append the file.
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

        // Shell-only extras (item id lists, hotkeys, show-command) mean nothing to us.
        foreach (string extra in new[] { "%*", "%2", "%3", "%4", "%5", "%6", "%7", "%8", "%9",
                                         "\"%I\"", "%I", "\"%i\"", "%i", "%D", "%d", "%W", "%w", "%v", "%V" })
            command = command.Replace(extra, string.Empty, StringComparison.Ordinal);

        command = command.Trim();
        return substituted ? command : $"{command} {quoted}";
    }

    private static string BuildHostCommand(string host, string app, string args)
    {
        string quoted = $"\"{app.Trim().Trim('"')}\"";
        string head = host switch
        {
            "cmd.exe"        => $"cmd.exe /c {quoted}",
            "powershell.exe" => $"powershell.exe -ExecutionPolicy Bypass -File {quoted}",
            "control.exe"    => $"control.exe {quoted}",
            // /s imports without the "are you sure" prompt. The caller already chose to
            // run this elevated, and a prompt on the interactive desktop would be the
            // only thing standing between them and a silent TrustedInstaller import
            // either way — so honour the request rather than half-asking.
            "regedit.exe"    => $"regedit.exe /s {quoted}",
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
            string? full = ResolveExecutable(app);
            if (full is null) return false;
            return ReadPeSubsystem(full) == NativeMethods.IMAGE_SUBSYSTEM_WINDOWS_CUI;
        }
        catch { return false; }
    }

    // Resolves a launch target to a full image path. A rooted path is returned
    // as-is if it exists; a bare name (e.g. "powershell.exe") is looked up on the
    // standard search path including PATH. Returns null if it cannot be resolved.
    // CreateProcessAsUser needs this because, unlike CreateProcess with a null
    // lpApplicationName, it does not search PATH for the application name itself.
    private static unsafe string? ResolveExecutable(string app)
    {
        string exe = app.Trim().Trim('"');
        if (exe.Length == 0) return null;
        if (exe.Contains('%')) exe = ExpandEnvVars(exe);

        if (exe.Contains('\\') || exe.Contains('/'))
            return System.IO.File.Exists(exe) ? exe : null;

        const int n = NativeMethods.MAX_PATH * 2;
        char* buf = stackalloc char[n];
        uint len = NativeMethods.SearchPathW(null, exe, ".exe", n, buf, IntPtr.Zero);
        return (len > 0 && len < n) ? new string(buf, 0, (int)len) : null;
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
