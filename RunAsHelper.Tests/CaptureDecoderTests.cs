using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RunAsHelper.Service.Core;
using Xunit;

namespace RunAsHelper.Tests;

// The service decodes each captured child-output line as strict UTF-8, then falls back to
// the console OEM code page when the bytes are not valid UTF-8. These pin both branches and
// the empty-line edge, using bytes that are invalid UTF-8 but meaningful in an OEM page.
public class CaptureDecoderTests
{
    [Fact]
    public void ValidUtf8_DecodesAsUtf8()
    {
        // "cafe" with an accented e (U+00E9): 0x63 0x61 0x66 0xC3 0xA9 is valid UTF-8.
        string cafe = "caf" + (char)0x00E9;
        byte[] bytes = Encoding.UTF8.GetBytes(cafe);
        Assert.Equal(cafe, CaptureDecoder.Decode(bytes, 1252));
    }

    [Fact]
    public void InvalidUtf8_FallsBackToCodePage1252()
    {
        // 0x97 is a lone UTF-8 continuation byte (invalid); in Windows-1252 it is U+2014 (em dash).
        byte[] bytes = { (byte)'a', 0x97, (byte)'b' };
        Assert.Equal("a" + (char)0x2014 + "b", CaptureDecoder.Decode(bytes, 1252));
    }

    [Fact]
    public void InvalidUtf8_FallsBackToCodePage437()
    {
        // 0xA4 is invalid standalone UTF-8; in OEM code page 437 it is U+00F1 (n with tilde).
        byte[] bytes = { 0xA4 };
        Assert.Equal(((char)0x00F1).ToString(), CaptureDecoder.Decode(bytes, 437));
    }

    [Fact]
    public void EmptyLine_IsEmptyString()
    {
        Assert.Equal(string.Empty, CaptureDecoder.Decode(Array.Empty<byte>(), 1252));
    }
}

// CaptureRelay owns the lifetime of one /capture session: it pumps the child's output to the
// caller while waiting for the child, and decides how the session ends. The Win32 parts are
// delegates here, so these drive the three endings the service must survive: the caller
// disconnecting mid-stream, the child exiting while a descendant still holds the pipe open,
// and the ceiling passing. Each run is guarded by a wall-clock limit because the failure
// mode these protect against is "waits forever".
public class CaptureRelayTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    // A read end that hands out queued chunks, then blocks until cancelled or until the
    // test completes it (EOF). Records disposal, which is part of the relay's contract.
    private sealed class ScriptedStream : Stream
    {
        private readonly Queue<byte[]> _chunks = new();
        private readonly TaskCompletionSource _eof = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public ScriptedStream(params string[] chunks)
        {
            foreach (string c in chunks) _chunks.Enqueue(Encoding.ASCII.GetBytes(c));
        }

        public void EndOfStream() => _eof.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Disposed) throw new ObjectDisposedException(nameof(ScriptedStream));
            if (_chunks.Count > 0)
            {
                byte[] chunk = _chunks.Dequeue();
                chunk.CopyTo(buffer);
                return chunk.Length;
            }
            await _eof.Task.WaitAsync(ct);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            _eof.TrySetResult();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<CaptureResult> RunGuarded(Task<CaptureResult> run)
    {
        Task first = await Task.WhenAny(run, Task.Delay(Guard));
        Assert.True(first == run, "CaptureRelay.RunAsync did not return within the guard: the session would hang the launch slot.");
        return await run;
    }

    // A child wait that only ends when its token is cancelled (the child never exits).
    private static Func<CancellationToken, Task<bool>> ChildNeverExits(TaskCompletionSource<bool> cancelled) =>
        async ct =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(true); throw; }
            return true;
        };

    [Fact]
    public async Task ClientDisconnects_MidStream_EndsTheSessionAndDisposesTheReadEnd()
    {
        // L3-14: the client's pipe is closed, so the first "stdout" frame fails. The relay
        // must stop waiting for the child (which never exits here) and dispose the read end
        // so the child's next write fails instead of blocking on a full pipe.
        var output = new ScriptedStream("line one\n");
        var waitCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int sends = 0;

        CaptureResult result = await RunGuarded(CaptureRelay.RunAsync(
            output,
            sendLine: (_, _) => { sends++; throw new IOException("Pipe is broken."); },
            waitForExit: ChildNeverExits(waitCancelled),
            oemCodePage: 437,
            drainGrace: CaptureRelay.DrainGrace,
            ct: CancellationToken.None));

        Assert.Equal(CaptureEnd.ClientGone, result.End);
        Assert.Equal(1, sends);
        Assert.True(output.Disposed, "the read end must be disposed so the child's writes fail fast");
        Assert.True(waitCancelled.Task.IsCompleted, "the child wait must be cancelled, not left running");
    }

    [Fact]
    public async Task ChildExits_ButDescendantHoldsThePipe_DrainIsCutShortAfterTheGrace()
    {
        // L3-03: the child exited at once, but the stream never reaches EOF because a
        // grandchild inherited the write end. The relay must return within the grace,
        // report the exit, and flag that the drain was cut short.
        var output = new ScriptedStream("done\n");
        var received = new List<string>();

        CaptureResult result = await RunGuarded(CaptureRelay.RunAsync(
            output,
            sendLine: (line, _) => { received.Add(line); return Task.CompletedTask; },
            waitForExit: _ => Task.FromResult(true),
            oemCodePage: 437,
            drainGrace: TimeSpan.FromMilliseconds(200),
            ct: CancellationToken.None));

        Assert.Equal(CaptureEnd.Exited, result.End);
        Assert.True(result.DrainCutShort);
        Assert.Equal(new[] { "done" }, received);
        Assert.True(output.Disposed);
    }

    [Fact]
    public async Task ChildExits_AndThePipeReachesEof_DrainCompletes()
    {
        var output = new ScriptedStream("a\r\nb");
        output.EndOfStream();
        var received = new List<string>();

        CaptureResult result = await RunGuarded(CaptureRelay.RunAsync(
            output,
            sendLine: (line, _) => { received.Add(line); return Task.CompletedTask; },
            waitForExit: _ => Task.FromResult(true),
            oemCodePage: 437,
            drainGrace: CaptureRelay.DrainGrace,
            ct: CancellationToken.None));

        Assert.Equal(CaptureEnd.Exited, result.End);
        Assert.False(result.DrainCutShort);
        Assert.Equal(new[] { "a", "b" }, received);
    }

    [Fact]
    public async Task CeilingPasses_StopsThePumpAndDisposesTheReadEnd()
    {
        // L3-02: on timeout the read end is closed and the pump ended before the relay
        // returns; the caller then closes its process handle before any frame is written.
        var output = new ScriptedStream("partial");
        var received = new List<string>();

        CaptureResult result = await RunGuarded(CaptureRelay.RunAsync(
            output,
            sendLine: (line, _) => { received.Add(line); return Task.CompletedTask; },
            waitForExit: async ct => { await Task.Delay(50, ct); return false; },
            oemCodePage: 437,
            drainGrace: CaptureRelay.DrainGrace,
            ct: CancellationToken.None));

        Assert.Equal(CaptureEnd.TimedOut, result.End);
        Assert.True(output.Disposed, "the read end must be disposed on timeout");
        Assert.Empty(received);
    }

    [Fact]
    public async Task PumpEofWhileChildStillRuns_WaitsForTheChild()
    {
        // The child closed its stdout but has not exited: EOF on the pipe is not the end
        // of the session; the exit wait still decides it.
        var output = new ScriptedStream("x\n");
        output.EndOfStream();
        var childExit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<CaptureResult> run = CaptureRelay.RunAsync(
            output,
            sendLine: (_, _) => Task.CompletedTask,
            waitForExit: _ => childExit.Task,
            oemCodePage: 437,
            drainGrace: CaptureRelay.DrainGrace,
            ct: CancellationToken.None);

        Task first = await Task.WhenAny(run, Task.Delay(300));
        Assert.True(first != run, "the relay must not end before the child does");
        childExit.SetResult(true);

        CaptureResult result = await RunGuarded(run);
        Assert.Equal(CaptureEnd.Exited, result.End);
        Assert.False(result.DrainCutShort);
    }
}
