using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace RunAsHelper.Tests;

/// <summary>
/// The help text is the single source for --help and the tray How to Use dialog.
/// These tests assert the v2.3.0 contract: the console launcher is named, the new
/// switches and exit codes appear, and no example still tells a shell user to type
/// the silent GUI exe. They are expected RED in slice C's worktree, where HelpText.cs
/// is still the 2.2.0 text, and go green once slice B rewrites it at integration.
/// That red run is the mutation evidence for these checks (see tests/MUTATIONS.md).
/// </summary>
public class HelpTextTests
{
    private static readonly string[] Lines =
        RunAsHelper.HelpText.Cli.Replace("\r\n", "\n").Split('\n');

    [Theory]
    [InlineData("RunAsHelper.com")]
    [InlineData("/trusted")]
    [InlineData("/ps:")]
    [InlineData("124")]
    public void Cli_MentionsNewFeature(string token)
    {
        Assert.Contains(token, RunAsHelper.HelpText.Cli);
    }

    [Fact]
    public void Cli_HasNoBareExeExampleLine()
    {
        // An example line that begins (after indentation) with "RunAsHelper.exe " tells a
        // shell caller to invoke the GUI binary, which returns immediately with no output.
        string[] offenders = Lines.Where(l => Regex.IsMatch(l, @"^\s+RunAsHelper\.exe ")).ToArray();
        Assert.True(
            offenders.Length == 0,
            "help text still shows RunAsHelper.exe example lines: " + string.Join(" | ", offenders));
    }

    [Theory]
    [InlineData("/p:")]
    [InlineData("/as:")]
    [InlineData("/capture")]
    [InlineData("/timeout:")]
    [InlineData("/jobs")]
    [InlineData("/kill:")]
    [InlineData("/joblog:")]
    [InlineData("/trusted")]
    [InlineData("/ps:")]
    public void Cli_DocumentsEveryParsedSwitch(string token)
    {
        // Every switch token Program.cs parses must be documented, so help never omits a
        // flag the parser accepts.
        Assert.Contains(token, RunAsHelper.HelpText.Cli);
    }
}
