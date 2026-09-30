using System;
using System.IO;
using System.Text.RegularExpressions;

namespace RunAsHelper.Core;

/// Which PowerShell edition should host a .ps1 target.
internal enum PowerShellEdition { Unspecified = 0, Desktop = 1, Core = 2 }

/// <summary>What a script's #Requires statements demand. Statement is the line that decided it.</summary>
internal readonly record struct PowerShellRequirement(PowerShellEdition Edition, string? Statement);

/// <summary>
/// Reads the #Requires statements at the head of a PowerShell script and reports which
/// edition they demand. A best-effort text scan (block comments and here-strings are
/// stripped first); PowerShell itself is the real enforcer, so a missed edge case only
/// changes which host is preferred, never correctness.
/// </summary>
internal static class PowerShellRequires
{
    internal const int MaxScanChars = 64 * 1024;

    private static readonly Regex BlockComment =
        new(@"<#.*?#>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex HereString =
        new(@"@[""']\r?\n.*?\r?\n[ \t]*[""']@", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex Statement =
        new(@"^[ \t]*#requires[ \t]+(?<args>[^\r\n]*)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VersionArg =
        new(@"-Version[ \t]+(?<v>\d+)(?:\.\d+)*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex EditionArg =
        new(@"-PSEdition[ \t]+(?<e>Core|Desktop)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static PowerShellRequirement Parse(string? scriptText)
    {
        if (string.IsNullOrEmpty(scriptText)) return new PowerShellRequirement(PowerShellEdition.Unspecified, null);

        string text = scriptText.Length > MaxScanChars ? scriptText[..MaxScanChars] : scriptText;
        // Strip block comments then here-strings so a #requires-looking line inside one is
        // not read as a real statement. Best effort: an unterminated or nested construct, or
        // a here-string that crosses the 64 KB boundary, is left as text and scanned anyway.
        text = BlockComment.Replace(text, string.Empty);
        text = HereString.Replace(text, string.Empty);

        string? desktopStatement = null;
        string? coreStatement = null;

        foreach (Match m in Statement.Matches(text))
        {
            string statement = m.Value.Trim();
            string args = m.Groups["args"].Value;

            Match edition = EditionArg.Match(args);
            if (edition.Success)
            {
                if (edition.Groups["e"].Value.Equals("Desktop", StringComparison.OrdinalIgnoreCase))
                    desktopStatement ??= statement;
                else
                    coreStatement ??= statement;   // Core
            }

            Match version = VersionArg.Match(args);
            if (version.Success && int.TryParse(version.Groups["v"].Value, out int major) && major >= 6)
                coreStatement ??= statement;
        }

        // An explicit edition beats a version number, and Desktop beats Core when a script
        // demands both. "-Version 5.1" alone stays Unspecified because pwsh satisfies it too.
        if (desktopStatement is not null) return new PowerShellRequirement(PowerShellEdition.Desktop, desktopStatement);
        if (coreStatement is not null) return new PowerShellRequirement(PowerShellEdition.Core, coreStatement);
        return new PowerShellRequirement(PowerShellEdition.Unspecified, null);
    }
}

/// <summary>The PowerShell host the caller's own shell implies (command line only).</summary>
internal sealed record CallerShellHint(string ImagePath, PowerShellEdition Edition);

/// <summary>The resolved host: its absolute path, a display label, the reason it was chosen, and an optional warning.</summary>
internal sealed record PowerShellHostChoice(string HostPath, string Label, string Reason, string? Warning);

/// <summary>
/// Picks the PowerShell host for a .ps1 target. Probes (reading the script head, locating
/// pwsh / Windows PowerShell, reading a product version) are injected so the whole decision
/// is unit-testable without a real machine.
/// </summary>
internal sealed class PowerShellHostResolver(
    Func<string, string?> readScriptHead,      // path -> first 64 KB, or null if unreadable/missing
    Func<string?>         findPwsh,            // absolute pwsh.exe path, or null
    Func<string?>         findWindowsPowerShell, // absolute powershell.exe path, or null
    Func<string, string?> productVersion)      // exe path -> "7.6.5", or null
{
    /// <summary>
    /// Precedence, first match wins: (1) the explicit /ps: switch, (2) a #Requires
    /// statement in the script, (3) the caller's own shell (command line only), (4) Windows
    /// PowerShell 5.1. Returns null only when even Windows PowerShell cannot be located, in
    /// which case the caller leaves the command line untouched and the service's own
    /// .ps1 -> powershell.exe mapping applies.
    /// </summary>
    public PowerShellHostChoice? Resolve(
        string scriptPath, PowerShellEdition explicitSwitch, Func<CallerShellHint?>? callerShell)
    {
        PowerShellEdition edition;
        string reason;
        string? callerImage = null;

        if (explicitSwitch != PowerShellEdition.Unspecified)
        {
            edition = explicitSwitch;
            reason = explicitSwitch == PowerShellEdition.Core ? "/ps:7" : "/ps:5";
        }
        else
        {
            PowerShellRequirement req = PowerShellRequires.Parse(readScriptHead(scriptPath));
            if (req.Edition != PowerShellEdition.Unspecified)
            {
                edition = req.Edition;
                reason = req.Statement ?? "#Requires";
            }
            else
            {
                CallerShellHint? hint = callerShell?.Invoke();
                if (hint is not null)
                {
                    edition = hint.Edition;
                    reason = "caller shell";
                    callerImage = hint.ImagePath;
                }
                else
                {
                    edition = PowerShellEdition.Desktop;
                    reason = "default";
                }
            }
        }

        string? warning = null;

        if (edition == PowerShellEdition.Core)
        {
            // The caller-shell path already names the exact pwsh image; otherwise search.
            string? path = (reason == "caller shell" ? callerImage : null) ?? findPwsh();
            if (path is null)
            {
                warning = $"pwsh requested by {reason} but not found (PATH, %ProgramFiles%\\PowerShell\\7); using Windows PowerShell 5.1";
                // Fall through to Desktop, keeping the original reason so the log still
                // explains why pwsh was wanted.
            }
            else
            {
                return new PowerShellHostChoice(path, $"pwsh {productVersion(path) ?? "7"}", reason, warning);
            }
        }

        // Desktop: always the real Windows PowerShell image, never powershell_ise.exe, even
        // when the caller shell was the ISE.
        string? wp = findWindowsPowerShell();
        if (wp is null) return null;
        return new PowerShellHostChoice(wp, "Windows PowerShell 5.1", reason, warning);
    }
}

/// <summary>Parses a /ps: switch value into an edition.</summary>
internal static class PowerShellSwitch
{
    /// "5" / "5.1" / "desktop" / "powershell" -> Desktop; "7" / "core" / "pwsh" -> Core; any
    /// bare integer 6+ -> Core, else Desktop; anything else is a usage error (returns false).
    public static bool TryParse(string value, out PowerShellEdition edition)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "5":
            case "5.1":
            case "desktop":
            case "powershell":
                edition = PowerShellEdition.Desktop;
                return true;
            case "7":
            case "core":
            case "pwsh":
                edition = PowerShellEdition.Core;
                return true;
        }

        if (int.TryParse(value.Trim(), out int major))
        {
            edition = major >= 6 ? PowerShellEdition.Core : PowerShellEdition.Desktop;
            return true;
        }

        edition = PowerShellEdition.Unspecified;
        return false;
    }
}

/// <summary>Rewrites a .ps1 launch command line to run under a chosen PowerShell host.</summary>
internal static class PowerShellScriptRewrite
{
    /// <summary>
    /// Splits a command line into its leading token (the target) and the remaining arguments,
    /// mirroring RunAsHelper.Service/Core/ElevationLauncher.cs ParseCommandLine so client and
    /// service agree on what the target is.
    /// </summary>
    public static (string App, string Args) Split(string commandLine)
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

    public static bool IsScriptTarget(string app)
        => Path.GetExtension(app.Trim().Trim('"')).Equals(".ps1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the rewritten command line, or null when the target is not a .ps1 or no host
    /// could be located. <paramref name="choice"/> is set whenever a host decision was made,
    /// so the caller can log it even when the rewrite itself is left null.
    /// </summary>
    public static string? TryRewrite(
        string commandLine, string workingDirectory,
        PowerShellEdition explicitSwitch, Func<CallerShellHint?>? callerShell,
        PowerShellHostResolver resolver,
        Func<string, string> expandEnvironment,
        out PowerShellHostChoice? choice)
    {
        choice = null;
        var (app, args) = Split(commandLine);
        if (!IsScriptTarget(app)) return null;

        string script = app.Trim().Trim('"');
        if (script.Contains('%')) script = expandEnvironment(script);
        // The 2.2.0 service expanded the WHOLE command line of a hosted .ps1 (its host
        // branch sends no separate arguments), so a saved entry's "-Out %ProgramData%\a.log"
        // reached the script expanded. The rewritten line below carries arguments, which the
        // service never expands, so expand them here with the same expander as the path.
        if (args.Contains('%')) args = expandEnvironment(args);

        // The path used to READ the script for #Requires. Usually the same as the emitted
        // script path; when a working directory is set, the emitted path stays relative (the
        // service applies that directory) but #Requires is read against it best-effort.
        string readPath = script;
        if (!Path.IsPathRooted(script))
        {
            if (workingDirectory.Length == 0)
            {
                // No working directory: resolve against the CLI's current directory now, so
                // the service (whose cwd is System32) still finds it.
                try { script = Path.GetFullPath(script); readPath = script; }
                catch { /* leave relative; the service reports a proper failure */ }
            }
            else
            {
                try { readPath = Path.Combine(expandEnvironment(workingDirectory), script); }
                catch { /* best effort; readScriptHead just returns null */ }
            }
        }

        choice = resolver.Resolve(readPath, explicitSwitch, callerShell);
        if (choice is null) return null;

        string rewritten = $"\"{choice.HostPath}\" -ExecutionPolicy Bypass -File \"{script}\"";
        return args.Length > 0 ? rewritten + " " + args : rewritten;
    }
}
