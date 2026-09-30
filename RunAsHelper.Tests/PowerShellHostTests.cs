using System;
using RunAsHelper.Core;
using Xunit;

namespace RunAsHelper.Tests;

public class PowerShellHostTests
{
    private const string Pwsh = @"C:\pwsh\pwsh.exe";
    private const string Wps  = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

    private static PowerShellHostResolver Resolver(
        string? scriptText = null, string? pwsh = Pwsh, string? wps = Wps, string version = "7.6.5")
        => new(
            readScriptHead: _ => scriptText,
            findPwsh: () => pwsh,
            findWindowsPowerShell: () => wps,
            productVersion: _ => version);

    // ---- #Requires parsing ----
    // Expected edition is passed as its underlying int because the enum is internal and a
    // public xunit test method cannot expose it in its signature.
    [Theory]
    [InlineData("#Requires -Version 7", (int)PowerShellEdition.Core)]
    [InlineData("#Requires -PSEdition Core", (int)PowerShellEdition.Core)]
    [InlineData("#Requires -PSEdition Desktop", (int)PowerShellEdition.Desktop)]
    [InlineData("#Requires -Version 5.1", (int)PowerShellEdition.Unspecified)]
    [InlineData("Write-Host hi", (int)PowerShellEdition.Unspecified)]
    public void Requires_ParsesEdition(string script, int expected)
        => Assert.Equal((PowerShellEdition)expected, PowerShellRequires.Parse(script).Edition);

    [Fact]
    public void Requires_DesktopBeatsCore_WhenBothPresent()
        => Assert.Equal(PowerShellEdition.Desktop,
            PowerShellRequires.Parse("#Requires -Version 7\n#Requires -PSEdition Desktop").Edition);

    [Fact]
    public void Requires_IgnoresStatementInsideBlockComment()
        => Assert.Equal(PowerShellEdition.Unspecified,
            PowerShellRequires.Parse("<#\n#Requires -Version 7\n#>\nWrite-Host hi").Edition);

    [Fact]
    public void Requires_IgnoresStatementInsideHereString()
        => Assert.Equal(PowerShellEdition.Unspecified,
            PowerShellRequires.Parse("$x = @'\n#Requires -Version 7\n'@\nWrite-Host hi").Edition);

    [Fact]
    public void Requires_IgnoresStatementBeyond64Kb()
    {
        string filler = new string('x', PowerShellRequires.MaxScanChars) + "\n#Requires -Version 7";
        Assert.Equal(PowerShellEdition.Unspecified, PowerShellRequires.Parse(filler).Edition);
    }

    [Fact]
    public void Requires_RecordsTheDecidingStatement()
        => Assert.Equal("#Requires -Version 7",
            PowerShellRequires.Parse("# a comment\n#Requires -Version 7\n").Statement);

    // ---- Precedence: switch, then #Requires, then caller shell, then default ----
    [Fact]
    public void Resolve_ExplicitSwitchBeatsRequires()
    {
        var choice = Resolver(scriptText: "#Requires -PSEdition Desktop")
            .Resolve(@"C:\s.ps1", PowerShellEdition.Core, callerShell: null);
        Assert.Equal(Pwsh, choice!.HostPath);
        Assert.Equal("/ps:7", choice.Reason);
    }

    [Fact]
    public void Resolve_RequiresBeatsCallerShell()
    {
        var choice = Resolver(scriptText: "#Requires -PSEdition Core")
            .Resolve(@"C:\s.ps1", PowerShellEdition.Unspecified,
                     callerShell: () => new CallerShellHint(Wps, PowerShellEdition.Desktop));
        Assert.Equal(Pwsh, choice!.HostPath);
        Assert.Contains("Requires", choice.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_CallerShellUsedWhenSwitchAndRequiresSilent()
    {
        var choice = Resolver(scriptText: "Write-Host hi")
            .Resolve(@"C:\s.ps1", PowerShellEdition.Unspecified,
                     callerShell: () => new CallerShellHint(@"C:\other\pwsh.exe", PowerShellEdition.Core));
        Assert.Equal(@"C:\other\pwsh.exe", choice!.HostPath);   // the caller's exact pwsh image
        Assert.Equal("caller shell", choice.Reason);
    }

    [Fact]
    public void Resolve_DefaultsToWindowsPowerShell()
    {
        var choice = Resolver(scriptText: "Write-Host hi")
            .Resolve(@"C:\s.ps1", PowerShellEdition.Unspecified, callerShell: null);
        Assert.Equal(Wps, choice!.HostPath);
        Assert.Equal("default", choice.Reason);
        Assert.Equal("Windows PowerShell 5.1", choice.Label);
    }

    [Fact]
    public void Resolve_FallsBackTo51_WhenPwshMissing()
    {
        var choice = Resolver(scriptText: null, pwsh: null)
            .Resolve(@"C:\s.ps1", PowerShellEdition.Core, callerShell: null);
        Assert.Equal(Wps, choice!.HostPath);
        Assert.Equal("/ps:7", choice.Reason);                 // the original reason is preserved
        Assert.NotNull(choice.Warning);
    }

    [Fact]
    public void Resolve_NullWhenNoHostFound()
        => Assert.Null(Resolver(pwsh: null, wps: null)
            .Resolve(@"C:\s.ps1", PowerShellEdition.Unspecified, callerShell: null));

    // ---- /ps: switch parsing ----
    [Theory]
    [InlineData("5", (int)PowerShellEdition.Desktop)]
    [InlineData("5.1", (int)PowerShellEdition.Desktop)]
    [InlineData("desktop", (int)PowerShellEdition.Desktop)]
    [InlineData("powershell", (int)PowerShellEdition.Desktop)]
    [InlineData("4", (int)PowerShellEdition.Desktop)]
    [InlineData("7", (int)PowerShellEdition.Core)]
    [InlineData("core", (int)PowerShellEdition.Core)]
    [InlineData("pwsh", (int)PowerShellEdition.Core)]
    [InlineData("6", (int)PowerShellEdition.Core)]
    [InlineData("8", (int)PowerShellEdition.Core)]
    public void Switch_Parses(string value, int expected)
    {
        Assert.True(PowerShellSwitch.TryParse(value, out var edition));
        Assert.Equal((PowerShellEdition)expected, edition);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("7.4")]
    [InlineData("")]
    public void Switch_RejectsUnknown(string value)
        => Assert.False(PowerShellSwitch.TryParse(value, out _));

    // ---- Rewrite ----
    [Fact]
    public void Rewrite_NonScriptTarget_ReturnsNull()
    {
        string? result = PowerShellScriptRewrite.TryRewrite(
            "notepad.exe", "", PowerShellEdition.Unspecified, null,
            Resolver(), s => s, out var choice);
        Assert.Null(result);
        Assert.Null(choice);
    }

    [Fact]
    public void Rewrite_RootedScript_ProducesHostFileCommand()
    {
        string? result = PowerShellScriptRewrite.TryRewrite(
            @"C:\scripts\fix.ps1 -Flag", "", PowerShellEdition.Core, null,
            Resolver(), s => s, out var choice);
        Assert.Equal($"\"{Pwsh}\" -ExecutionPolicy Bypass -File \"C:\\scripts\\fix.ps1\" -Flag", result);
        Assert.NotNull(choice);
    }

    [Fact]
    public void Rewrite_ExpandsEnvironmentInScriptPath()
    {
        string? result = PowerShellScriptRewrite.TryRewrite(
            @"%SCRIPTS%\fix.ps1", "", PowerShellEdition.Core, null,
            Resolver(), s => s.Replace("%SCRIPTS%", @"C:\scripts"), out _);
        Assert.Contains(@"-File ""C:\scripts\fix.ps1""", result);
    }

    [Fact]
    public void Rewrite_ExpandsEnvironmentInArguments()
    {
        // The 2.2.0 service expanded the whole hosted .ps1 command line, arguments included;
        // the client-side rewrite has to do the same or "-Out %X%\a.log" reaches the script
        // literally. Killing mutation: drop the args expansion in TryRewrite.
        string? result = PowerShellScriptRewrite.TryRewrite(
            @"C:\scripts\fix.ps1 -Out %X%\a.log", "", PowerShellEdition.Core, null,
            Resolver(), s => s.Replace("%X%", @"C:\data"), out _);
        Assert.EndsWith(@"-File ""C:\scripts\fix.ps1"" -Out C:\data\a.log", result);
        Assert.DoesNotContain("%X%", result);
    }

    [Fact]
    public void Rewrite_MakesRelativePathAbsolute_WhenNoWorkingDirectory()
    {
        string? result = PowerShellScriptRewrite.TryRewrite(
            "fix.ps1", "", PowerShellEdition.Core, null, Resolver(), s => s, out _);
        string expected = System.IO.Path.GetFullPath("fix.ps1");
        Assert.Contains($"-File \"{expected}\"", result);
    }

    [Fact]
    public void Rewrite_KeepsRelativePath_WhenWorkingDirectorySet()
    {
        string? result = PowerShellScriptRewrite.TryRewrite(
            "fix.ps1", @"C:\work", PowerShellEdition.Core, null, Resolver(), s => s, out _);
        Assert.Contains("-File \"fix.ps1\"", result);
    }
}
