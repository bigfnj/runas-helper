// ConPTY harness for tests: runs a command line under a real pseudo console, so the
// child's stdout is a genuine console handle (FILE_TYPE_CHAR), captures what the
// console rendered, optionally delivers Ctrl+C, and returns the exit code.
// Compiled with Add-Type from ConPty.ps1 (pwsh 7).
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

public static class ConPty
{
    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEXW { public STARTUPINFOW StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);
    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFOEXW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;
    private const uint WAIT_TIMEOUT = 0x00000102;
    private const uint TERMINATED_BY_HARNESS = 0xDEAD;

    public sealed class Result
    {
        public string Text;
        public string RawText;
        public int ExitCode;
        public bool TimedOut;
    }

    public static Result Run(string commandLine, int timeoutMs, int ctrlCAfterMs, string workingDirectory)
    {
        IntPtr inRead, inWrite, outRead, outWrite;
        if (!CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0)) throw new System.ComponentModel.Win32Exception();
        if (!CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0)) throw new System.ComponentModel.Win32Exception();

        IntPtr hPC;
        int hr = CreatePseudoConsole(new COORD { X = 200, Y = 60 }, inRead, outWrite, 0, out hPC);
        if (hr != 0) throw new System.ComponentModel.Win32Exception(hr, "CreatePseudoConsole failed");
        // The pseudo console holds its own references to these two ends.
        CloseHandle(inRead);
        CloseHandle(outWrite);

        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        IntPtr attrList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
            throw new System.ComponentModel.Win32Exception();
        if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception();

        var siex = new STARTUPINFOEXW();
        siex.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEXW));
        siex.lpAttributeList = attrList;

        // Drain the console's output on a background thread from the start, or the
        // console blocks once its pipe buffer is full.
        var sb = new StringBuilder();
        var reader = new Thread(() =>
        {
            using (var fs = new FileStream(new SafeFileHandle(outRead, true), FileAccess.Read, 4096, false))
            {
                var buf = new byte[4096];
                var dec = Encoding.UTF8.GetDecoder();
                try
                {
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        var chars = new char[dec.GetCharCount(buf, 0, n)];
                        int c = dec.GetChars(buf, 0, n, chars, 0);
                        lock (sb) sb.Append(chars, 0, c);
                    }
                }
                catch (IOException) { }
            }
        });
        reader.IsBackground = true;
        reader.Start();

        PROCESS_INFORMATION pi;
        var cmd = new StringBuilder(commandLine);
        // PowerShell passes "" for a null [string]; CreateProcessW rejects "" with error 123.
        string cwd = string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory;
        bool ok = CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT,
                                 IntPtr.Zero, cwd, ref siex, out pi);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            ClosePseudoConsole(hPC);
            throw new System.ComponentModel.Win32Exception(err,
                "CreateProcessW failed (Win32 error " + err + "): " + new System.ComponentModel.Win32Exception(err).Message);
        }
        CloseHandle(pi.hThread);

        bool timedOut = false;
        if (ctrlCAfterMs > 0)
        {
            if (WaitForSingleObject(pi.hProcess, (uint)ctrlCAfterMs) == WAIT_TIMEOUT)
            {
                uint written;
                WriteFile(inWrite, new byte[] { 0x03 }, 1, out written, IntPtr.Zero);
            }
        }
        if (WaitForSingleObject(pi.hProcess, (uint)timeoutMs) == WAIT_TIMEOUT)
        {
            timedOut = true;
            TerminateProcess(pi.hProcess, TERMINATED_BY_HARNESS);
            WaitForSingleObject(pi.hProcess, 5000);
        }
        uint code;
        GetExitCodeProcess(pi.hProcess, out code);
        CloseHandle(pi.hProcess);

        // Closing the pseudo console flushes its output and closes our read end.
        ClosePseudoConsole(hPC);
        DeleteProcThreadAttributeList(attrList);
        Marshal.FreeHGlobal(attrList);
        CloseHandle(inWrite);
        reader.Join(10000);

        string raw;
        lock (sb) raw = sb.ToString();
        string text = Regex.Replace(raw,
            @"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07]*(\x07|\x1b\\)|\x1b[()][0-9A-Za-z]|\x1b[=>]|\r", "");
        return new Result { Text = text, RawText = raw, ExitCode = unchecked((int)code), TimedOut = timedOut };
    }
}
