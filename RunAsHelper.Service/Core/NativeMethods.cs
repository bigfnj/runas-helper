using System;
using System.Runtime.InteropServices;

namespace RunAsHelper.Service.Core;

internal static partial class NativeMethods
{
    // ── Access rights ────────────────────────────────────────────────────
    internal const uint READ_CONTROL              = 0x00020000;
    internal const uint STANDARD_RIGHTS_REQUIRED  = 0x000F0000;
    internal const uint MAXIMUM_ALLOWED           = 0x02000000;
    internal const uint TOKEN_ASSIGN_PRIMARY      = 0x00000001;
    internal const uint TOKEN_DUPLICATE           = 0x00000002;
    internal const uint TOKEN_IMPERSONATE         = 0x00000004;
    internal const uint TOKEN_QUERY               = 0x00000008;
    internal const uint TOKEN_QUERY_SOURCE        = 0x00000010;
    internal const uint TOKEN_ADJUST_PRIVILEGES   = 0x00000020;
    internal const uint TOKEN_ADJUST_GROUPS       = 0x00000040;
    internal const uint TOKEN_ADJUST_DEFAULT      = 0x00000080;
    internal const uint TOKEN_ADJUST_SESSIONID    = 0x00000100;
    internal const uint TOKEN_ALL_ACCESS           =
        STANDARD_RIGHTS_REQUIRED | TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE |
        TOKEN_IMPERSONATE | TOKEN_QUERY | TOKEN_QUERY_SOURCE |
        TOKEN_ADJUST_PRIVILEGES | TOKEN_ADJUST_GROUPS | TOKEN_ADJUST_DEFAULT |
        TOKEN_ADJUST_SESSIONID;

    internal const uint SYNCHRONIZE                      = 0x00100000;
    internal const uint PROCESS_DUP_HANDLE               = 0x00000040;
    internal const uint PROCESS_QUERY_INFORMATION        = 0x00000400;
    // Lighter-weight than PROCESS_QUERY_INFORMATION; sufficient for
    // QueryFullProcessImageNameW and OpenProcessToken(TOKEN_QUERY).
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x00001000;
    internal const uint THREAD_DIRECT_IMPERSONATION       = 0x00000200;

    // ── Privilege names ──────────────────────────────────────────────────
    internal const string SE_DEBUG_NAME       = "SeDebugPrivilege";
    internal const string SE_IMPERSONATE_NAME = "SeImpersonatePrivilege";
    // Required to call SetTokenInformation(TokenSessionId).
    internal const string SE_TCB_NAME         = "SeTcbPrivilege";
    // Required by CreateProcessAsUser to assign a primary token to the new
    // process and charge its quota to the target account.
    internal const string SE_ASSIGNPRIMARYTOKEN_NAME = "SeAssignPrimaryTokenPrivilege";
    internal const string SE_INCREASE_QUOTA_NAME     = "SeIncreaseQuotaPrivilege";
    internal const uint SE_PRIVILEGE_ENABLED  = 0x00000002;

    // ── Process creation ─────────────────────────────────────────────────
    internal const uint LOGON_WITH_PROFILE        = 0x00000001;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    // Allocate a fresh console for the child. Required for console-subsystem
    // programs (cmd.exe, powershell.exe) launched from a service — without it
    // they get no window the user can type into.
    internal const uint CREATE_NEW_CONSOLE         = 0x00000010;

    // PE optional-header subsystem value for console (character-mode) apps.
    internal const ushort IMAGE_SUBSYSTEM_WINDOWS_CUI = 3;

    // STARTUPINFO.dwFlags: honor wShowWindow. Plus the SW_* show-window states
    // the client maps a saved app's "Windows State" onto.
    internal const uint   STARTF_USESHOWWINDOW = 0x00000001;
    internal const ushort SW_HIDE              = 0;
    internal const ushort SW_SHOWNORMAL        = 1;
    internal const ushort SW_SHOWMAXIMIZED     = 3;
    internal const ushort SW_SHOWMINNOACTIVE   = 7;

    // ── Priority classes ─────────────────────────────────────────────────
    internal const uint NORMAL_PRIORITY_CLASS       = 0x00000020;

    // ── Service ──────────────────────────────────────────────────────────
    internal const uint ERROR_SERVICE_ALREADY_RUNNING = 0x00000420;
    internal const uint SC_MANAGER_ALL_ACCESS    = 0x000F003F;
    internal const uint SERVICE_START            = 0x00000010;
    internal const uint SERVICE_QUERY_STATUS     = 0x00000004;
    internal const uint SC_STATUS_PROCESS_INFO   = 0;

    // ── FormatMessage ─────────────────────────────────────────────────────
    internal const uint FORMAT_MESSAGE_FROM_SYSTEM    = 0x00001000;
    internal const uint FORMAT_MESSAGE_FROM_HMODULE   = 0x00000800;
    internal const uint FORMAT_MESSAGE_IGNORE_INSERTS = 0x00000200;

    // ── Toolhelp ──────────────────────────────────────────────────────────
    internal const uint TH32CS_SNAPPROCESS = 0x00000002;
    internal const uint TH32CS_SNAPTHREAD  = 0x00000004;

    // ── Misc ─────────────────────────────────────────────────────────────
    internal const int STATUS_SUCCESS = 0;
    internal const int MAX_PATH       = 260;
    internal static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    // ── Enums ─────────────────────────────────────────────────────────────

    internal enum SecurityImpersonationLevel
    {
        SecurityAnonymous      = 0,
        SecurityIdentification = 1,
        SecurityImpersonation  = 2,
        SecurityDelegation     = 3,
    }

    internal enum TokenType
    {
        TokenPrimary       = 1,
        TokenImpersonation = 2,
    }

    internal enum ServiceState : uint
    {
        Stopped         = 1,
        StartPending    = 2,
        StopPending     = 3,
        Running         = 4,
        ContinuePending = 5,
        PausePending    = 6,
        Paused          = 7,
    }

    // Used to query/set token properties. TokenUser identifies the account the
    // token represents (used by post-install validation); TokenSessionId remaps
    // a launched process to the interactive desktop (Session 0 → console session).
    internal enum TOKEN_INFORMATION_CLASS
    {
        TokenUser       = 1,
        TokenGroups     = 2,
        TokenPrivileges = 3,
        TokenSessionId  = 12,
        TokenElevation  = 20,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_ELEVATION
    {
        public uint TokenIsElevated;
    }

    // ── Blittable structs ─────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        public uint LowPart;
        public int  HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint   Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_PRIVILEGES
    {
        public uint                PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privilege;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint   dwProcessId;
        public uint   dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOW
    {
        public uint   cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint   dwX;
        public uint   dwY;
        public uint   dwXSize;
        public uint   dwYSize;
        public uint   dwXCountChars;
        public uint   dwYCountChars;
        public uint   dwFillAttribute;
        public uint   dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SERVICE_STATUS_PROCESS
    {
        public uint         dwServiceType;
        public ServiceState dwCurrentState;
        public uint         dwControlsAccepted;
        public uint         dwWin32ExitCode;
        public uint         dwServiceSpecificExitCode;
        public uint         dwCheckPoint;
        public uint         dwWaitHint;
        public uint         dwProcessId;
        public uint         dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_ATTRIBUTES
    {
        public uint   nLength;
        public IntPtr lpSecurityDescriptor;
        public int    bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_QUALITY_OF_SERVICE
    {
        public uint                       Length;
        public SecurityImpersonationLevel ImpersonationLevel;
        public byte                       ContextTrackingMode;
        public byte                       EffectiveOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int  tpBasePri;
        public int  tpDeltaPri;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct PROCESSENTRY32W
    {
        public uint   dwSize;
        public uint   cntUsage;
        public uint   th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint   th32ModuleID;
        public uint   cntThreads;
        public uint   th32ParentProcessID;
        public int    pcPriClassBase;
        public uint   dwFlags;
        public fixed char szExeFile[MAX_PATH];

        public readonly string ExeName
        {
            get { fixed (char* p = szExeFile) return new string(p); }
        }
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    internal static partial void Sleep(uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenThread(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwThreadId);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr LoadLibraryW(string lpLibFileName);

    // Returns the session ID of the physical console (the currently logged-in interactive user).
    [LibraryImport("kernel32.dll")]
    internal static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentProcessId();

    // PID of the process at the other end of a named pipe — used to bind the
    // CLI-allow gate to the tray that enabled it (lazy liveness check).
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(IntPtr Pipe, out uint ClientProcessId);

    // Returns the full image path of a running process. Used to verify that the
    // pipe client is the installed RunAsHelper tray binary (H-1 identity check).
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool QueryFullProcessImageNameW(
        IntPtr  hProcess,
        uint    dwFlags,
        char*   lpExeName,
        ref uint lpdwSize);

    // Maps a process id to its Terminal Services session — used to log which
    // session the service itself runs in (expected 0) vs. the launch target.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static unsafe partial uint FormatMessageW(
        uint   dwFlags,
        IntPtr lpSource,
        uint   dwMessageId,
        uint   dwLanguageId,
        char*  lpBuffer,
        uint   nSize,
        IntPtr Arguments);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static unsafe partial uint ExpandEnvironmentStringsW(
        string lpSrc,
        char*  lpDst,
        uint   nSize);

    // Resolves a bare executable name (e.g. "powershell.exe") to a full path
    // using the standard search order, so we can inspect its PE subsystem.
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static unsafe partial uint SearchPathW(
        string? lpPath,
        string  lpFileName,
        string? lpExtension,
        uint    nBufferLength,
        char*   lpBuffer,
        IntPtr  lpFilePart);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool Process32FirstW(IntPtr hSnapshot, PROCESSENTRY32W* lppe);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool Process32NextW(IntPtr hSnapshot, PROCESSENTRY32W* lppe);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool Thread32First(IntPtr hSnapshot, THREADENTRY32* lpte);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool Thread32Next(IntPtr hSnapshot, THREADENTRY32* lpte);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(
        IntPtr ProcessHandle,
        uint   DesiredAccess,
        out IntPtr TokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenThreadToken(
        IntPtr hThread,
        uint   dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bOpenAsSelf,
        out IntPtr phToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ImpersonateLoggedOnUser(IntPtr hToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RevertToSelf();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryServiceStatusEx(
        IntPtr  hService,
        uint    InfoLevel,
        out SERVICE_STATUS_PROCESS lpBuffer,
        uint    cbBufSize,
        out uint pcbBytesNeeded);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool StartServiceW(
        IntPtr hService,
        uint   dwNumServiceArgs,
        IntPtr lpServiceArgVectors);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr OpenSCManagerW(
        string? lpMachineName,
        string? lpDatabaseName,
        uint    dwDesiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr OpenServiceW(
        IntPtr hSCManager,
        string lpServiceName,
        uint   dwDesiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool LookupPrivilegeValueW(
        string? lpSystemName,
        string  lpName,
        LUID*   lpLuid);

    // Resolves a privilege LUID back to its name (e.g. "SeDebugPrivilege") for
    // the verbose token-privilege dump during validation.
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool LookupPrivilegeNameW(
        string?  lpSystemName,
        LUID*    lpLuid,
        char*    lpName,
        ref uint cchName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool AdjustTokenPrivileges(
        IntPtr             TokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
        TOKEN_PRIVILEGES*  NewState,
        uint               BufferLength,
        TOKEN_PRIVILEGES*  PreviousState,
        uint*              ReturnLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool DuplicateTokenEx(
        IntPtr               hExistingToken,
        uint                 dwDesiredAccess,
        SECURITY_ATTRIBUTES* lpTokenAttributes,
        SecurityImpersonationLevel ImpersonationLevel,
        TokenType            TokenType,
        out IntPtr           phNewToken);

    // Sets a property on a token. Used to change TokenSessionId so a process
    // launched from Session 0 appears on the interactive desktop.
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool SetTokenInformation(
        IntPtr                  TokenHandle,
        TOKEN_INFORMATION_CLASS TokenInformationClass,
        void*                   TokenInformation,
        uint                    TokenInformationLength);

    // Unlike CreateProcessWithTokenW (which places the child in the CALLER's
    // session), CreateProcessAsUser creates the process in the session carried
    // by the token — so a Session 0 service can launch onto the interactive
    // desktop. Requires SeAssignPrimaryToken + SeIncreaseQuota on the caller.
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool CreateProcessAsUserW(
        IntPtr              hToken,
        string?             lpApplicationName,
        string?             lpCommandLine,
        IntPtr              lpProcessAttributes,
        IntPtr              lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint                dwCreationFlags,
        IntPtr              lpEnvironment,
        string?             lpCurrentDirectory,
        STARTUPINFOW*       lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("ntdll.dll")]
    internal static unsafe partial int NtImpersonateThread(
        IntPtr                       hThread,
        IntPtr                       hThreadToImpersonate,
        SECURITY_QUALITY_OF_SERVICE* SecurityQualityOfService);

    // ── Token identity (post-install validation) ─────────────────────────

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetTokenInformation(
        IntPtr                  TokenHandle,
        TOKEN_INFORMATION_CLASS TokenInformationClass,
        void*                   TokenInformation,
        uint                    TokenInformationLength,
        out uint                ReturnLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ConvertSidToStringSidW(IntPtr Sid, out IntPtr StringSid);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool LookupAccountSidW(
        string?  lpSystemName,
        IntPtr   Sid,
        char*    Name,
        ref uint cchName,
        char*    ReferencedDomainName,
        ref uint cchReferencedDomainName,
        out int  peUse);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr LocalFree(IntPtr hMem);

    internal static unsafe bool IsTokenElevated(IntPtr hToken)
    {
        TOKEN_ELEVATION elev = default;
        uint len;
        return GetTokenInformation(
                   hToken, TOKEN_INFORMATION_CLASS.TokenElevation,
                   &elev, (uint)sizeof(TOKEN_ELEVATION), out len)
               && elev.TokenIsElevated != 0;
    }
}
