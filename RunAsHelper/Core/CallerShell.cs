using System;
using System.IO;
using System.Text.RegularExpressions;

namespace RunAsHelper.Core;

/// <summary>
/// Finds the shell that invoked the CLI by walking the parent-process chain, so a .ps1
/// target can be hosted by the same PowerShell edition the caller is using. Every failure
/// path returns null (the host rule then falls back to Windows PowerShell 5.1); it never
/// throws.
/// </summary>
internal static class CallerShell
{
    // Wrappers that sit between the caller's shell and this process are skipped so the
    // real shell is found: the console launcher, the exe itself, and conhost.
    private static readonly string[] Skip = ["RunAsHelper.com", "RunAsHelper.exe", "conhost.exe"];
    private const int MaxDepth = 8;

    /// <summary>
    /// The nearest ancestor that is a PowerShell host: pwsh.exe -> Core (that exact image),
    /// powershell.exe or powershell_ise.exe -> Desktop. Null when the chain cannot be read, a
    /// PID looks reused, or the nearest non-skipped ancestor is not a PowerShell host.
    /// </summary>
    public static CallerShellHint? Detect()
    {
        IntPtr hCur = NativeMethods.GetCurrentProcess();   // pseudo-handle; must never be closed
        bool ownCur = false;
        long curCreated = CreationTime(hCur);

        try
        {
            for (int depth = 0; depth < MaxDepth; depth++)
            {
                uint parentPid = ParentPid(hCur);
                if (parentPid == 0) return null;

                IntPtr hParent = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, parentPid);
                if (hParent == IntPtr.Zero) return null;

                long parentCreated = CreationTime(hParent);
                // A real parent is older than its child. A zero or later creation time means
                // the PID was reused since the child started, so the chain cannot be trusted.
                if (parentCreated == 0 || parentCreated > curCreated)
                {
                    NativeMethods.CloseHandle(hParent);
                    return null;
                }

                string? image = ImagePath(hParent);

                if (ownCur) NativeMethods.CloseHandle(hCur);
                hCur = hParent;
                ownCur = true;
                curCreated = parentCreated;

                if (image is null) return null;
                string name = Path.GetFileName(image);
                if (Array.Exists(Skip, s => s.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                return name.ToLowerInvariant() switch
                {
                    "pwsh.exe" => new CallerShellHint(image, PowerShellEdition.Core),
                    "powershell.exe" or "powershell_ise.exe"
                        => new CallerShellHint(image, PowerShellEdition.Desktop),
                    _ => null,
                };
            }
            return null;
        }
        catch { return null; }
        finally { if (ownCur) NativeMethods.CloseHandle(hCur); }
    }

    private static unsafe uint ParentPid(IntPtr hProcess)
    {
        NativeMethods.PROCESS_BASIC_INFORMATION pbi = default;
        uint returnLength;
        int status = NativeMethods.NtQueryInformationProcess(
            hProcess, NativeMethods.ProcessBasicInformation,
            &pbi, (uint)sizeof(NativeMethods.PROCESS_BASIC_INFORMATION), &returnLength);
        if (status != 0) return 0;
        return (uint)pbi.InheritedFromUniqueProcessId.ToInt64();
    }

    private static long CreationTime(IntPtr hProcess)
        => NativeMethods.GetProcessTimes(hProcess, out long creation, out _, out _, out _) ? creation : 0;

    private static unsafe string? ImagePath(IntPtr hProcess)
    {
        const int n = 1024;
        char* buf = stackalloc char[n];
        uint size = n;
        return NativeMethods.QueryFullProcessImageNameW(hProcess, 0, buf, ref size)
            ? new string(buf, 0, (int)size)
            : null;
    }
}

/// <summary>The production probes wired into a <see cref="PowerShellHostResolver"/>.</summary>
internal static class PowerShellHostProbes
{
    public static readonly PowerShellHostResolver Resolver =
        new(ReadHead, FindPwsh, FindWindowsPowerShell, ProductVersion);

    // First 64 KB of the script as text, honouring a byte-order mark. Any failure
    // (missing, locked, unreadable) yields null, and the host rule proceeds without #Requires.
    private static string? ReadHead(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            char[] buf = new char[PowerShellRequires.MaxScanChars];
            int read = reader.ReadBlock(buf, 0, buf.Length);
            return new string(buf, 0, read);
        }
        catch { return null; }
    }

    // pwsh on the client PATH first, then the default per-machine install location.
    private static string? FindPwsh()
    {
        string? onPath = NativeMethods.ResolvePath("pwsh.exe");
        if (onPath is not null) return onPath;
        string candidate = Path.Combine(
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\PowerShell\7"), "pwsh.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? FindWindowsPowerShell()
    {
        string candidate = Path.Combine(
            Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\WindowsPowerShell\v1.0"), "powershell.exe");
        if (File.Exists(candidate)) return candidate;
        return NativeMethods.ResolvePath("powershell.exe");
    }

    private static readonly Regex VersionNumber =
        new(@"\d+\.\d+(?:\.\d+)?", RegexOptions.CultureInvariant);

    // pwsh reports ProductVersion like "7.6.5 SHA: ...", so take the leading version number.
    private static string? ProductVersion(string path)
    {
        try
        {
            string? v = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion;
            if (string.IsNullOrWhiteSpace(v)) return null;
            Match m = VersionNumber.Match(v);
            return m.Success ? m.Value : null;
        }
        catch { return null; }
    }
}
