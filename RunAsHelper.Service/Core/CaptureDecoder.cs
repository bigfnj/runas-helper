using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RunAsHelper.Service.Core;

/// <summary>
/// Decodes one captured child-output line. The child's stdout arrives as raw bytes:
/// most tools emit UTF-8, but cmd.exe and Windows PowerShell 5.1 emit the console OEM
/// code page (437 on a US box), whose accented bytes are not valid UTF-8 and used to
/// come back as replacement characters. Each line is decoded as strict UTF-8 first; a
/// line that is not valid UTF-8 is decoded again with the caller-supplied OEM code page.
/// Pure and dependency-free (base class library only) so it links into the unit tests.
/// </summary>
internal static class CaptureDecoder
{
    // Encoding.GetEncoding for an OEM code page (437, 850, 1252, ...) needs the code-pages
    // provider registered. Doing it here, once, gives the service and the unit tests the
    // same behaviour with no separate setup step. RegisterProvider is idempotent.
    static CaptureDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // Strict UTF-8: throws on any byte sequence that is not valid UTF-8, which is the
    // signal to fall back. Encoding.UTF8 would replace such bytes silently, hiding the
    // very case the fallback exists for, so a throwing instance is used.
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decode <paramref name="lineBytes"/> as UTF-8, falling back to code page
    /// <paramref name="oemCodePage"/> when the bytes are not valid UTF-8. An empty
    /// input yields an empty string.
    /// </summary>
    public static string Decode(byte[] lineBytes, int oemCodePage)
    {
        if (lineBytes.Length == 0) return string.Empty;
        try
        {
            return StrictUtf8.GetString(lineBytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(oemCodePage).GetString(lineBytes);
        }
    }
}

/// <summary>
/// Splits a byte stream into lines the way the 2.2.0 StreamReader did: CR, LF and CRLF all
/// end a line, so a progress redraw written with a bare CR arrives as its own line, and a
/// run of CRs before an LF (the <c>\r\r\n</c> a console app produces when cmd redirects its
/// handle) is one terminator, not a line plus an empty line. A line never carries a
/// trailing CR. Bytes are fed in whatever chunks the pipe delivers; a terminator split
/// across two chunks is handled. A line longer than <see cref="MaxLineBytes"/> is emitted
/// in pieces so that no single frame, string or buffer ever grows past the cap. Pure and
/// base-class-library only so it links into the unit tests.
/// </summary>
internal sealed class LineSplitter
{
    /// <summary>The longest line emitted in one piece (1 MiB).</summary>
    public const int MaxLineBytes = 1024 * 1024;

    private readonly MemoryStream _buf;
    private readonly int _cap;

    // CR bytes seen since the last content byte. They end the line only once the next
    // byte shows what follows: another CR extends the run, an LF completes a CRLF, and a
    // content byte starts the next line.
    private int _pendingCr;

    // True right after a line was emitted because it hit the cap. The terminator that
    // follows (if the line was exactly cap bytes long) then ends nothing, rather than
    // producing a spurious empty line.
    private bool _afterPartial;

    public LineSplitter(int maxLineBytes = MaxLineBytes)
    {
        if (maxLineBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxLineBytes));
        _cap = maxLineBytes;
        _buf = new MemoryStream();
    }

    /// <summary>
    /// Consumes one chunk and appends every completed line (without its terminator) to
    /// <paramref name="lines"/>, in order.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> chunk, List<byte[]> lines)
    {
        foreach (byte b in chunk)
        {
            if (b == (byte)'\n')
            {
                _pendingCr = 0;
                EmitLine(lines);
            }
            else if (b == (byte)'\r')
            {
                _pendingCr++;
            }
            else
            {
                if (_pendingCr > 0)
                {
                    _pendingCr = 0;
                    EmitLine(lines);
                }
                _afterPartial = false;
                _buf.WriteByte(b);
                if (_buf.Length >= _cap)
                {
                    lines.Add(_buf.ToArray());
                    _buf.SetLength(0);
                    _afterPartial = true;
                }
            }
        }
    }

    /// <summary>
    /// The final line at end of stream, or null when nothing is pending. A stream that
    /// ends in a CR run yields the text before it; a stream that ends in a complete line
    /// yields null.
    /// </summary>
    public byte[]? Flush()
    {
        if (_buf.Length == 0 && (_pendingCr == 0 || _afterPartial))
        {
            _pendingCr = 0;
            return null;
        }
        _pendingCr = 0;
        _afterPartial = false;
        byte[] line = _buf.ToArray();
        _buf.SetLength(0);
        return line;
    }

    private void EmitLine(List<byte[]> lines)
    {
        if (_buf.Length == 0 && _afterPartial)
        {
            _afterPartial = false;
            return;
        }
        _afterPartial = false;
        lines.Add(_buf.ToArray());
        _buf.SetLength(0);
    }
}

/// <summary>How a capture session ended.</summary>
internal enum CaptureEnd
{
    /// <summary>The child exited inside the ceiling; its exit code can be read.</summary>
    Exited,
    /// <summary>The ceiling passed first; the child keeps running.</summary>
    TimedOut,
    /// <summary>
    /// The caller stopped accepting frames (its pipe closed) while output was streaming.
    /// The output stream has been disposed so the child's writes fail instead of blocking
    /// on a full pipe; the child keeps running and nothing more is sent.
    /// </summary>
    ClientGone,
}

/// <summary>
/// Outcome of <see cref="CaptureRelay.RunAsync"/>. <see cref="DrainCutShort"/> is true when
/// the child exited but its output stream did not reach EOF within the grace period, which
/// happens when a descendant inherited the write end and kept it open.
/// </summary>
internal sealed record CaptureResult(CaptureEnd End, bool DrainCutShort);

/// <summary>
/// Streams a child's merged stdout/stderr to the caller line by line while waiting for the
/// child to exit, and decides how the session ends. Everything Win32 (the process handle,
/// the wait, the exit code) is supplied by the caller as delegates, so this class is base
/// class library only and links into the unit tests. Contract:
/// <list type="bullet">
/// <item>The output stream is always disposed before this returns.</item>
/// <item>No frame is sent after the outcome is decided; the caller sends the exit or timeout
/// frames afterwards, best effort, once it has closed its own handles.</item>
/// <item>If the caller vanishes mid-stream, the wait is cancelled and the read end is disposed
/// so the child's next write fails with a broken pipe instead of blocking forever.</item>
/// <item>After the child exits, the remaining output is drained for at most
/// <c>drainGrace</c>; a descendant holding the pipe open cannot extend the ceiling.</item>
/// </list>
/// </summary>
internal static class CaptureRelay
{
    /// <summary>How long to keep reading after the child has exited.</summary>
    public static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(3);

    /// <summary>Why the pump loop stopped.</summary>
    internal enum PumpEnd { Eof, Cancelled, ReadFailed, ClientGone }

    /// <param name="output">The read end of the capture pipe. Disposed before returning.</param>
    /// <param name="sendLine">Delivers one decoded line to the caller. An IOException or
    /// ObjectDisposedException from it means the caller is gone.</param>
    /// <param name="waitForExit">Completes true when the child exits, false when the ceiling
    /// passes; honours its token.</param>
    /// <param name="oemCodePage">Fallback code page for lines that are not valid UTF-8.</param>
    /// <param name="drainGrace">How long to keep draining after the child exits.</param>
    /// <param name="ct">The service stop token; cancelling it propagates.</param>
    public static async Task<CaptureResult> RunAsync(
        Stream output,
        Func<string, CancellationToken, Task> sendLine,
        Func<CancellationToken, Task<bool>> waitForExit,
        int oemCodePage,
        TimeSpan drainGrace,
        CancellationToken ct)
    {
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task<PumpEnd> pump = Task.Run(() => PumpAsync(output, sendLine, oemCodePage, pumpCts.Token), CancellationToken.None);
        Task<bool> wait = waitForExit(waitCts.Token);

        try
        {
            Task first = await Task.WhenAny(pump, wait);

            if (first == pump && pump.Result == PumpEnd.ClientGone)
            {
                // Nobody is left to receive the output. Stop waiting for the child (the
                // caller's slot is released by its own finally) and close the read end so
                // the child's writes fail fast rather than filling the pipe and blocking.
                waitCts.Cancel();
                try { await wait; } catch (OperationCanceledException) { }
                return new CaptureResult(CaptureEnd.ClientGone, DrainCutShort: false);
            }

            // Either the child exited or timed out, or the pump reached EOF or a read
            // error while the child still runs (it closed its stdout); wait for the child.
            bool exited = await wait;
            if (!exited)
            {
                pumpCts.Cancel();
                await pump;
                return new CaptureResult(CaptureEnd.TimedOut, DrainCutShort: false);
            }

            // The child is gone. Drain what it left in the pipe, but only for a bounded
            // grace: a descendant that inherited the write end can keep the pipe from
            // reaching EOF for as long as it lives, and /timeout is a ceiling.
            pumpCts.CancelAfter(drainGrace);
            PumpEnd end = await pump;
            return new CaptureResult(CaptureEnd.Exited, DrainCutShort: end == PumpEnd.Cancelled);
        }
        finally
        {
            pumpCts.Cancel();
            try { await pump; } catch (Exception) { }
            await output.DisposeAsync();
        }
    }

    // Reads the pipe in chunks, splits them into lines and sends each. Never throws: the
    // reason it stopped is the return value, so the caller can tell a dead client (send
    // failed) from a closed pipe (read failed or EOF) and from its own cancellation.
    private static async Task<PumpEnd> PumpAsync(
        Stream output, Func<string, CancellationToken, Task> sendLine, int oemCodePage, CancellationToken ct)
    {
        var chunk = new byte[4096];
        var splitter = new LineSplitter();
        var lines = new List<byte[]>();
        while (true)
        {
            int read;
            try
            {
                read = await output.ReadAsync(chunk.AsMemory(), ct);
            }
            catch (OperationCanceledException) { return PumpEnd.Cancelled; }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return PumpEnd.ReadFailed; }

            if (read == 0)
            {
                if (splitter.Flush() is byte[] tail)
                {
                    PumpEnd sent = await SendAsync(tail);
                    if (sent != PumpEnd.Eof) return sent;
                }
                return PumpEnd.Eof;
            }

            splitter.Feed(chunk.AsSpan(0, read), lines);
            foreach (byte[] line in lines)
            {
                PumpEnd sent = await SendAsync(line);
                if (sent != PumpEnd.Eof) return sent;
            }
            lines.Clear();
        }

        // Eof here means "sent, keep going"; anything else is the reason to stop.
        async Task<PumpEnd> SendAsync(byte[] line)
        {
            string text = CaptureDecoder.Decode(line, oemCodePage);
            try
            {
                await sendLine(text, ct);
                return PumpEnd.Eof;
            }
            catch (OperationCanceledException) { return PumpEnd.Cancelled; }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return PumpEnd.ClientGone; }
        }
    }
}
