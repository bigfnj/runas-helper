using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RunAsHelper.Launcher;

internal enum LaunchMode { Gui, Console }

/// <summary>
/// The launcher's decisions, kept free of Win32 so they can be unit-tested.
/// </summary>
internal static class LauncherLogic
{
    // The tray's own entry points (RunAsHelper/Program.cs: --revalidate|/validate, and the
    // sole-argument --activate, --tray, --postinstall). These open a window, so the launcher
    // hands them to the exe and does not wait.
    private static readonly string[] GuiOnlyFlags =
        ["--tray", "--activate", "--postinstall", "--revalidate", "/validate"];

    internal static LaunchMode Classify(string[] args) =>
        args.Length == 0 ||
        (args.Length == 1 && GuiOnlyFlags.Contains(args[0], StringComparer.OrdinalIgnoreCase))
            ? LaunchMode.Gui
            : LaunchMode.Console;

    /// <summary>
    /// Returns everything after argv[0] in a raw command line, untouched. argv[0] follows the
    /// CommandLineToArgvW rule: a leading quote runs to the next quote, otherwise it ends at
    /// the first whitespace. The tail keeps its own quoting exactly as the shell wrote it.
    /// </summary>
    internal static string StripArgv0(string raw)
    {
        int i = 0, n = raw.Length;
        while (i < n && char.IsWhiteSpace(raw[i])) i++;
        if (i < n && raw[i] == '"')
        {
            i++;
            while (i < n && raw[i] != '"') i++;
            if (i < n) i++;
        }
        else
        {
            while (i < n && !char.IsWhiteSpace(raw[i])) i++;
        }
        while (i < n && char.IsWhiteSpace(raw[i])) i++;
        return raw[i..];
    }

    /// <summary>"RunAsInvoker", appended to any compatibility layers the caller already set.</summary>
    internal static string MergeCompatLayer(string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing)) return "RunAsInvoker";
        var layers = existing.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return layers.Contains("RunAsInvoker", StringComparer.OrdinalIgnoreCase)
            ? existing
            : existing.Trim() + " RunAsInvoker";
    }

    /// <summary>
    /// A Unicode environment block: "NAME=value\0" per entry, sorted the way Windows keeps
    /// them (ordinal, ignoring case), with the closing extra '\0'.
    /// </summary>
    internal static char[] BuildEnvironmentBlock(IDictionary<string, string> env)
    {
        var sb = new StringBuilder();
        foreach (var kv in env.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');
        return sb.ToString().ToCharArray();
    }
}
