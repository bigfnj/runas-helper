using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace RunAsHelper.Tests;

/// <summary>
/// The help text is the single source for --help and the tray How to Use dialog.
/// These tests assert the v2.3.0 contract: the console launcher is named, the new
/// switches and exit codes appear, and no example still tells a shell user to type
/// the silent GUI exe. They were RED against the 2.2.0 HelpText.cs and went green when
/// slice B rewrote it; that red run is the mutation evidence (see tests/MUTATIONS.md).
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

    // ---- Every switch Program.cs parses is documented ----
    //
    // The token list is DERIVED from RunAsHelper/Program.cs at test time, not hand-kept:
    // every StartsWith("/...") and Equals("/...") / Equals("--...") literal in the dispatch
    // and the flag loop is a switch a user can type, and each must appear in HelpText.Cli.
    // A new switch added to the parser without a help line fails here. The three internal
    // hand-off flags are excluded on purpose: the tray, the installer and the elevation
    // hand-off pass them, a user never types them, and the help must not invite it.

    private static readonly string[] InternalHandoffFlags = { "--tray", "--activate", "--postinstall" };

    private static readonly Regex SwitchLiteral = new(
        @"(?:StartsWith|Equals)\(""(?<token>(?:/|--)[A-Za-z?][A-Za-z:?]*)""",
        RegexOptions.CultureInvariant);

    private static IReadOnlyList<string> ParsedSwitches()
    {
        string source = RepoSource.Read(@"RunAsHelper\Program.cs");
        return SwitchLiteral.Matches(source)
            .Select(m => m.Groups["token"].Value)
            .Where(t => !InternalHandoffFlags.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public void Cli_DocumentsEveryParsedSwitch()
    {
        IReadOnlyList<string> switches = ParsedSwitches();
        string[] missing = switches.Where(t => !RunAsHelper.HelpText.Cli.Contains(t, StringComparison.Ordinal)).ToArray();
        Assert.True(
            missing.Length == 0,
            "Program.cs parses switches the help does not mention: " + string.Join(", ", missing));
    }

    [Fact]
    public void Cli_DerivedSwitchList_HasTheKnownTokens()
    {
        // Control: the extraction must at least find the nine switches the 2.3.0 help
        // documents, or an extraction regex that matches nothing would pass the test above.
        string[] known = { "/p:", "/as:", "/capture", "/timeout:", "/jobs", "/kill:", "/joblog:", "/trusted", "/ps:" };
        IReadOnlyList<string> switches = ParsedSwitches();
        string[] notFound = known.Where(k => !switches.Contains(k, StringComparer.OrdinalIgnoreCase)).ToArray();
        Assert.True(
            notFound.Length == 0,
            "the derived switch list lost known tokens: " + string.Join(", ", notFound)
            + " (derived: " + string.Join(", ", switches) + ")");
    }

    // ---- Wording pinned by the 2.3.1 audit fixes ----

    [Fact]
    public void Cli_PsSwitchOverridesRequires()
    {
        // L1-02: /ps: wins over #Requires (PowerShellHostResolver.Resolve checks the switch
        // first); the 2.3.0 help said the opposite.
        string psLine = string.Join(" ", Lines.SkipWhile(l => !l.TrimStart().StartsWith("/ps:5|/ps:7")).Take(3));
        Assert.Contains("Overrides a #Requires", psLine);
        Assert.DoesNotContain("Ignored when the script", psLine);
    }

    [Theory]
    [InlineData(@"/trusted:remove <SID|DOMAIN\user>")]   // C0-04: names are accepted, say so
    [InlineData("no longer tracked")]                     // L4-01: a timed-out child leaves /jobs
    [InlineData("Without /capture")]                      // L1-06: /timeout needs /capture
    public void Cli_CarriesCorrectedWording(string token)
    {
        Assert.Contains(token, RunAsHelper.HelpText.Cli);
    }

    [Fact]
    public void Cli_DoesNotSayTimedOutChildIsInJobs()
    {
        // L4-01: on expiry the service releases the job and the slot, so "(see /jobs)" was
        // a promise the code does not keep.
        Assert.DoesNotContain("(see /jobs)", RunAsHelper.HelpText.Cli);
    }
}
