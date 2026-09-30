using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RunAsHelper.Service.Core;
using Xunit;

namespace RunAsHelper.Tests;

// The capture pump splits the child's byte stream into lines with LineSplitter. These pin
// the 2.2.0 StreamReader line semantics the 2.3.0 byte pump lost (CR, LF and CRLF all end a
// line, a CR run before LF is one terminator), the chunk-boundary case a pipe produces, the
// final partial line at EOF, and the 1 MiB cap that bounds every downstream allocation.
public class LineSplitterTests
{
    private static List<string> Split(params string[] chunks)
    {
        var splitter = new LineSplitter();
        var lines = new List<byte[]>();
        foreach (string chunk in chunks)
            splitter.Feed(Encoding.ASCII.GetBytes(chunk), lines);
        if (splitter.Flush() is byte[] tail) lines.Add(tail);
        return lines.Select(Encoding.ASCII.GetString).ToList();
    }

    [Fact]
    public void BareCr_EndsALine()
    {
        // A progress redraw: 2.2.0 delivered "a" and "b" as two frames.
        Assert.Equal(new[] { "a", "b" }, Split("a\rb\n"));
    }

    [Fact]
    public void CrCrLf_IsOneTerminator()
    {
        // cmd redirecting a console app's handle produces \r\r\n; the content must not
        // keep a CR and no empty line is invented for the second CR.
        Assert.Equal(new[] { "a" }, Split("a\r\r\n"));
    }

    [Fact]
    public void CrLf_ThenPartialLine_AtEof()
    {
        Assert.Equal(new[] { "a", "b" }, Split("a\r\nb"));
    }

    [Fact]
    public void FinalLineWithoutTerminator_IsFlushedAtEof()
    {
        Assert.Equal(new[] { "abc" }, Split("abc"));
    }

    [Fact]
    public void CompleteLine_LeavesNothingToFlush()
    {
        var splitter = new LineSplitter();
        var lines = new List<byte[]>();
        splitter.Feed(Encoding.ASCII.GetBytes("abc\n"), lines);
        Assert.Single(lines);
        Assert.Null(splitter.Flush());
    }

    [Fact]
    public void Terminator_SplitAcrossChunks_IsOneTerminator()
    {
        // The pipe hands over 4 KiB chunks; a CRLF can straddle two of them.
        Assert.Equal(new[] { "a", "b" }, Split("a\r", "\nb\n"));
    }

    [Fact]
    public void CrRun_SplitAcrossChunks_IsOneTerminator()
    {
        Assert.Equal(new[] { "a", "b" }, Split("a\r", "\r", "\nb"));
    }

    [Fact]
    public void LfAlone_IsAnEmptyLine()
    {
        Assert.Equal(new[] { "", "x" }, Split("\nx"));
    }

    [Fact]
    public void LineAboveTheCap_IsEmittedInPieces()
    {
        var splitter = new LineSplitter();
        var lines = new List<byte[]>();
        byte[] huge = new byte[LineSplitter.MaxLineBytes + 3];
        Array.Fill(huge, (byte)'a');
        splitter.Feed(huge, lines);
        splitter.Feed(new[] { (byte)'\n' }, lines);

        Assert.Equal(2, lines.Count);
        Assert.Equal(LineSplitter.MaxLineBytes, lines[0].Length);
        Assert.Equal(3, lines[1].Length);
        Assert.Null(splitter.Flush());
    }

    [Fact]
    public void LineExactlyAtTheCap_DoesNotProduceAnEmptyLine()
    {
        var splitter = new LineSplitter();
        var lines = new List<byte[]>();
        byte[] exact = new byte[LineSplitter.MaxLineBytes];
        Array.Fill(exact, (byte)'a');
        splitter.Feed(exact, lines);
        splitter.Feed(Encoding.ASCII.GetBytes("\r\nnext\n"), lines);

        Assert.Equal(new[] { LineSplitter.MaxLineBytes, 4 }, lines.Select(l => l.Length));
        Assert.Equal("next", Encoding.ASCII.GetString(lines[1]));
    }

    [Fact]
    public void SmallCap_IsHonoured()
    {
        var splitter = new LineSplitter(maxLineBytes: 4);
        var lines = new List<byte[]>();
        splitter.Feed(Encoding.ASCII.GetBytes("abcdefghij\n"), lines);
        Assert.Equal(new[] { "abcd", "efgh", "ij" }, lines.Select(Encoding.ASCII.GetString));
    }
}
