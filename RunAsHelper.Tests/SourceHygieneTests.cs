using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace RunAsHelper.Tests;

/// <summary>
/// Locates repository source files from the test assembly's directory, so a test can read
/// a file the project does not link (Program.cs pulls in WinForms and cannot be linked).
/// The root is the directory that holds RunAsHelper.sln, found by walking up from
/// AppContext.BaseDirectory (bin\Release\net10.0-windows\ under RunAsHelper.Tests).
/// </summary>
internal static class RepoSource
{
    private static readonly Lazy<string> RootDir = new(FindRoot);

    public static string Root => RootDir.Value;

    public static string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RunAsHelper.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "RunAsHelper.sln not found above " + AppContext.BaseDirectory + "; these tests read repository sources.");
    }
}

/// <summary>
/// Text that reaches a pipe frame or a console must be ASCII. The service used to write an
/// em dash as one Windows-1252 byte (0x97) when its stdout was a pipe, which a UTF-8 reader
/// shows as a replacement character (BL-27); the 2.3.0 sweep still missed one wire frame and
/// one client line (L4-12). This test reads the four files whose strings reach the wire or
/// the console and fails on any non-ASCII character inside a string literal of a statement
/// that builds a PipeMessage, calls Log / log?.Invoke, or writes to the console. ILogger
/// templates (logger.LogWarning and friends) stay free to use any character: they go to the
/// event log and the debugger, never down the pipe.
/// </summary>
public class SourceHygieneTests
{
    private static readonly string[] WireFiles =
    {
        @"RunAsHelper.Service\Worker\PipeServer.cs",
        @"RunAsHelper.Service\Core\ElevationLauncher.cs",
        @"RunAsHelper\Core\PipeClient.cs",
        @"RunAsHelper\Program.cs",
    };

    // The call shapes whose string arguments reach a pipe frame or a console.
    private static readonly string[] WireCalls =
    {
        "PipeMessage(", "Log(", "log?.Invoke(", "Console.WriteLine(", "Console.Error.WriteLine(",
    };

    // Regular, interpolated and verbatim string literals. Interpolation holes are not parsed:
    // the check only asks whether a character above 0x7F sits inside quotes, which is the
    // same question for all three forms.
    private static readonly Regex StringLiteral = new(
        @"\$?@?""(?:[^""\\]|\\.)*""",
        RegexOptions.CultureInvariant);

    public static IEnumerable<object[]> Files() => WireFiles.Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(Files))]
    public void WireStrings_AreAscii(string relativePath)
    {
        string source = RepoSource.Read(relativePath);
        var offenders = new List<string>();

        foreach (string call in WireCalls)
        {
            int at = 0;
            while ((at = source.IndexOf(call, at, StringComparison.Ordinal)) >= 0)
            {
                // The statement runs from the call to its terminating semicolon, so a literal
                // on a continuation line is still inspected.
                int end = source.IndexOf(';', at);
                if (end < 0) end = source.Length;
                foreach (Match literal in StringLiteral.Matches(source[at..end]))
                {
                    char bad = literal.Value.FirstOrDefault(c => c > 0x7F);
                    if (bad == default) continue;
                    int line = LineOf(source, at + literal.Index);
                    offenders.Add($"{relativePath}:{line} U+{(int)bad:X4} in {literal.Value.Trim()}");
                }
                at = end;
            }
        }

        Assert.True(
            offenders.Count == 0,
            "non-ASCII text in a string that reaches a pipe frame or the console:\n" + string.Join("\n", offenders));
    }

    private static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }
}
