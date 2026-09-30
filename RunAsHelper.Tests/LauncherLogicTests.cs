using System;
using System.Collections.Generic;
using Xunit;
using RunAsHelper.Launcher;

namespace RunAsHelper.Tests;

// Covers the launcher's pure decisions (LauncherLogic.cs, linked into this project).
// These run without Win32, so they pin the command-line handling that the shim relies on
// before any process is created. The design's tests T1-T3 name the mutation each set kills.
public class LauncherLogicTests
{
    // T1: no arguments and each GUI-only flag alone (case-insensitive) are Gui; a flag with
    // an extra argument, an ordinary switch, or a program name is Console.
    [Fact]
    public void Classify_NoArgs_IsGui()
    {
        Assert.Equal(LaunchMode.Gui, LauncherLogic.Classify(Array.Empty<string>()));
    }

    [Theory]
    [InlineData("--tray")]
    [InlineData("--activate")]
    [InlineData("--postinstall")]
    [InlineData("--revalidate")]
    [InlineData("/validate")]
    [InlineData("--TRAY")]
    [InlineData("/Validate")]
    public void Classify_SoleGuiFlag_IsGui(string flag)
    {
        Assert.Equal(LaunchMode.Gui, LauncherLogic.Classify(new[] { flag }));
    }

    [Fact]
    public void Classify_ConsoleForms_AreConsole()
    {
        Assert.Equal(LaunchMode.Console, LauncherLogic.Classify(new[] { "--tray", "extra" }));
        Assert.Equal(LaunchMode.Console, LauncherLogic.Classify(new[] { "--help" }));
        Assert.Equal(LaunchMode.Console, LauncherLogic.Classify(new[] { "/jobs" }));
        Assert.Equal(LaunchMode.Console, LauncherLogic.Classify(new[] { "cmd.exe" }));
        Assert.Equal(LaunchMode.Console, LauncherLogic.Classify(new[] { "/validate", "now" }));
    }

    // T2: the raw tail after argv[0] survives byte for byte. argv[0] follows the
    // CommandLineToArgvW rule (a leading quote runs to the next quote), the tail keeps its
    // own quoting and internal whitespace, leading whitespace before argv[0] is skipped.
    [Fact]
    public void StripArgv0_QuotedPathWithSpaces_KeepsTailVerbatim()
    {
        string raw = "\"C:\\Program Files\\RunAsHelper\\RunAsHelper.com\" /as:system cmd /c echo \"a  b\" \\\"c\\\"";
        Assert.Equal("/as:system cmd /c echo \"a  b\" \\\"c\\\"", LauncherLogic.StripArgv0(raw));
    }

    [Fact]
    public void StripArgv0_BareName_KeepsTail()
    {
        Assert.Equal("/jobs", LauncherLogic.StripArgv0("RunAsHelper /jobs"));
    }

    [Fact]
    public void StripArgv0_Argv0Only_IsEmpty()
    {
        Assert.Equal("", LauncherLogic.StripArgv0("RunAsHelper.com"));
        Assert.Equal("", LauncherLogic.StripArgv0("\"C:\\path with space\\RunAsHelper.com\""));
    }

    [Fact]
    public void StripArgv0_LeadingSpaces_SkippedBeforeArgv0()
    {
        Assert.Equal("/jobs", LauncherLogic.StripArgv0("   RunAsHelper.com /jobs"));
    }

    [Fact]
    public void StripArgv0_TrailingTailWhitespace_Kept()
    {
        Assert.Equal("/jobs  ", LauncherLogic.StripArgv0("RunAsHelper.com /jobs  "));
    }

    // T3 (part): MergeCompatLayer adds RunAsInvoker, appends to an existing layer, and is a
    // no-op when it is already present.
    [Fact]
    public void MergeCompatLayer_Null_IsRunAsInvoker()
    {
        Assert.Equal("RunAsInvoker", LauncherLogic.MergeCompatLayer(null));
        Assert.Equal("RunAsInvoker", LauncherLogic.MergeCompatLayer(""));
        Assert.Equal("RunAsInvoker", LauncherLogic.MergeCompatLayer("   "));
    }

    [Fact]
    public void MergeCompatLayer_ExistingOtherLayer_Appends()
    {
        Assert.Equal("HighDpiAware RunAsInvoker", LauncherLogic.MergeCompatLayer("HighDpiAware"));
    }

    [Fact]
    public void MergeCompatLayer_AlreadyPresent_Unchanged()
    {
        Assert.Equal("RunAsInvoker", LauncherLogic.MergeCompatLayer("RunAsInvoker"));
        Assert.Equal("HighDpiAware RunAsInvoker", LauncherLogic.MergeCompatLayer("HighDpiAware RunAsInvoker"));
    }

    // T3 (part): the environment block is "NAME=value\0" per entry, sorted ordinal-ignore-case,
    // and closed by a second null.
    [Fact]
    public void BuildEnvironmentBlock_SortedNullSeparatedDoubleNullTerminated()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["B"] = "2",
            ["A"] = "1",
        };
        string block = new string(LauncherLogic.BuildEnvironmentBlock(env));
        Assert.Equal("A=1\0B=2\0\0", block);
    }

    [Fact]
    public void BuildEnvironmentBlock_OrdinalIgnoreCaseOrder()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["path"] = "p",
            ["ALLUSERSPROFILE"] = "a",
            ["__COMPAT_LAYER"] = "RunAsInvoker",
        };
        string block = new string(LauncherLogic.BuildEnvironmentBlock(env));
        // Underscore (0x5F) sorts after the letters ordinal, so __COMPAT_LAYER comes last.
        Assert.Equal("ALLUSERSPROFILE=a\0path=p\0__COMPAT_LAYER=RunAsInvoker\0\0", block);
        Assert.Contains("__COMPAT_LAYER=RunAsInvoker\0", block);
        Assert.EndsWith("\0\0", block);
    }
}
