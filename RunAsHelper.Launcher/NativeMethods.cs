using System;
using System.Runtime.InteropServices;

namespace RunAsHelper.Launcher;

internal static unsafe partial class NativeMethods
{
    internal const int  STD_INPUT_HANDLE  = -10;
    internal const int  STD_OUTPUT_HANDLE = -11;
    internal const int  STD_ERROR_HANDLE  = -12;

    internal const uint FILE_TYPE_UNKNOWN = 0x0000;
    internal const uint FILE_TYPE_CHAR    = 0x0002;

    internal const uint HANDLE_FLAG_INHERIT        = 0x00000001;
    internal const uint STARTF_USESTDHANDLES       = 0x00000100;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    internal const uint INFINITE                   = 0xFFFFFFFF;

    internal const uint STATUS_CONTROL_C_EXIT = 0xC000013A;
    internal const uint CTRL_C_EVENT     = 0;
    internal const uint CTRL_BREAK_EVENT = 1;
    internal const uint CTRL_CLOSE_EVENT = 2;

    internal const uint CP_UTF8 = 65001;
    internal const int  ERROR_FILE_NOT_FOUND = 2;

    // Same layout as the service's STARTUPINFOW (RunAsHelper.Service/Core/NativeMethods.cs).
    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOW
    {
        public uint   cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public uint   dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint   dwProcessId, dwThreadId;
    }

    [LibraryImport("kernel32.dll")]
    internal static partial char* GetCommandLineW();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetFileType(IntPtr hFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    // lpCommandLine must be a writable buffer: CreateProcessW may modify it in place.
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcessW(
        string? lpApplicationName,
        char*   lpCommandLine,
        IntPtr  lpProcessAttributes,
        IntPtr  lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint    dwCreationFlags,
        char*   lpEnvironment,
        string? lpCurrentDirectory,
        STARTUPINFOW* lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleCtrlHandler(delegate* unmanaged<uint, int> handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetConsoleOutputCP();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleOutputCP(uint wCodePageID);

    internal static bool IsValidHandle(IntPtr h) => h != IntPtr.Zero && h != new IntPtr(-1);
}
