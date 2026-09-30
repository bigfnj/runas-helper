using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using static RunAsHelper.Launcher.NativeMethods;

namespace RunAsHelper.Launcher;

// RunAsHelper.com: a console-subsystem front door for RunAsHelper.exe.
//
// RunAsHelper.exe is one binary for the tray and the command line, built as a GUI
// application, and a shell never waits for a GUI application: a bare call returns before
// anything prints and sets no exit code. This launcher is what the shell runs instead
// (.COM precedes .EXE in PATHEXT). It starts RunAsHelper.exe from its own folder with the
// same command line and the same standard handles, waits, and exits with the exe's code.
//
// It runs at the caller's level and passes that level on. The exe's manifest asks for
// elevation, which a non-elevated shell cannot grant, so the RunAsInvoker compatibility
// layer is set for the child: from an elevated shell the child is elevated, from a normal
// shell it is not, and the service decides what that caller may do.
internal static unsafe class Program
{
    private static IntPtr s_child;
    private static int    s_interrupted;

    private static int Main(string[] args)
    {
        string dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string exe = Path.Combine(dir, "RunAsHelper.exe");
        if (!File.Exists(exe))
        {
            Console.Error.WriteLine($"RunAsHelper.com: RunAsHelper.exe was not found in {dir}. Reinstall RunAS Helper.");
            return ERROR_FILE_NOT_FOUND;
        }

        return LauncherLogic.Classify(args) == LaunchMode.Gui
            ? StartGui(exe, dir, args)
            : RunConsole(exe);
    }

    // Tray entry points open a window; hand them to the exe through the shell (so its own
    // manifest applies) and return at once.
    private static int StartGui(string exe, string dir, string[] args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe, args.Length == 1 ? args[0] : string.Empty)
            {
                UseShellExecute  = true,
                WorkingDirectory = dir,
            });
            return 0;
        }
        catch (Win32Exception ex)
        {
            Console.Error.WriteLine($"RunAsHelper.com: could not start \"{exe}\": {ex.Message} (error {ex.NativeErrorCode}).");
            return ex.NativeErrorCode == 0 ? 1 : ex.NativeErrorCode;
        }
    }

    private static int RunConsole(string exe)
    {
        // The raw tail, never re-quoted: args[] has already been through argv splitting and
        // re-joining it would change what the exe sees.
        string tail = LauncherLogic.StripArgv0(new string(GetCommandLineW()));
        string cmd  = tail.Length == 0 ? $"\"{exe}\"" : $"\"{exe}\" {tail}";

        // RunAsInvoker in the launcher's own environment and in the block handed to the
        // child, so the layer applies whichever of the two the loader consults.
        Environment.SetEnvironmentVariable("__COMPAT_LAYER",
            LauncherLogic.MergeCompatLayer(Environment.GetEnvironmentVariable("__COMPAT_LAYER")));
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? string.Empty;
        char[] block = LauncherLogic.BuildEnvironmentBlock(env);

        IntPtr hIn  = GetStdHandle(STD_INPUT_HANDLE);
        IntPtr hOut = GetStdHandle(STD_OUTPUT_HANDLE);
        IntPtr hErr = GetStdHandle(STD_ERROR_HANDLE);
        // A redirected handle is not guaranteed inheritable; without this the child sees no
        // stdout and falls back to attaching a console, which is exactly the silent case.
        foreach (IntPtr h in new[] { hIn, hOut, hErr })
            if (IsValidHandle(h)) SetHandleInformation(h, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);

        // The child is not attached to this console, so it cannot read the console's code
        // page and writes UTF-8. Make the console decode UTF-8 while it runs, then restore.
        bool consoleOut = IsValidHandle(hOut) && GetFileType(hOut) == FILE_TYPE_CHAR;
        uint prevCp = 0;
        if (consoleOut)
        {
            prevCp = GetConsoleOutputCP();
            if (prevCp != 0 && prevCp != CP_UTF8) SetConsoleOutputCP(CP_UTF8); else prevCp = 0;
        }

        // Registered before the child exists; a Ctrl+C that races creation is handled below.
        SetConsoleCtrlHandler(&OnCtrl, true);

        var si = new STARTUPINFOW
        {
            cb         = (uint)sizeof(STARTUPINFOW),
            dwFlags    = STARTF_USESTDHANDLES,
            hStdInput  = hIn,
            hStdOutput = hOut,
            hStdError  = hErr,
        };

        char[] cmdBuf = (cmd + '\0').ToCharArray();
        PROCESS_INFORMATION pi;
        bool ok;
        fixed (char* pCmd = cmdBuf)
        fixed (char* pEnv = block)
        {
            ok = CreateProcessW(exe, pCmd, IntPtr.Zero, IntPtr.Zero, true,
                                CREATE_UNICODE_ENVIRONMENT, pEnv, null, &si, out pi);
        }
        if (!ok)
        {
            int err = Marshal.GetLastPInvokeError();
            Console.Error.WriteLine($"RunAsHelper.com: could not start \"{exe}\": {new Win32Exception(err).Message} (error {err}).");
            if (prevCp != 0) SetConsoleOutputCP(prevCp);
            return err == 0 ? 1 : err;
        }
        CloseHandle(pi.hThread);
        Volatile.Write(ref s_child, pi.hProcess);
        if (Volatile.Read(ref s_interrupted) != 0)
            TerminateProcess(pi.hProcess, STATUS_CONTROL_C_EXIT);

        try
        {
            WaitForSingleObject(pi.hProcess, INFINITE);
            if (!GetExitCodeProcess(pi.hProcess, out uint code)) return 1;
            if (code == STATUS_CONTROL_C_EXIT && Volatile.Read(ref s_interrupted) != 0)
                Console.Error.WriteLine("RunAsHelper.com: interrupted; RunAsHelper.exe was stopped. The elevated target may still be running: see RunAsHelper /jobs.");
            return unchecked((int)code);
        }
        finally
        {
            Volatile.Write(ref s_child, IntPtr.Zero);
            CloseHandle(pi.hProcess);
            if (prevCp != 0) SetConsoleOutputCP(prevCp);
        }
    }

    // The exe is a GUI process and never receives console control events, so this is the
    // only place that reacts to Ctrl+C, Ctrl+Break and the console closing.
    [UnmanagedCallersOnly]
    private static int OnCtrl(uint ctrlType)
    {
        Volatile.Write(ref s_interrupted, 1);
        IntPtr h = Volatile.Read(ref s_child);
        if (h != IntPtr.Zero) TerminateProcess(h, STATUS_CONTROL_C_EXIT);
        // Handled for Ctrl+C/Break (the wait below returns with the child's code); for a
        // console close let Windows finish this process.
        return ctrlType is CTRL_C_EVENT or CTRL_BREAK_EVENT ? 1 : 0;
    }
}
