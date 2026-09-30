using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RunAsHelper.Shared.Protocol;
using Xunit;

namespace RunAsHelper.Tests;

public class PipeMessageWriterTests
{
    // Yields inside WriteAsync so two concurrent writers overlap unless something serialises
    // them; records the peak number of writers seen inside WriteAsync at once.
    private sealed class YieldingStream : Stream
    {
        private readonly object _lock = new();
        private int _active;
        public int MaxConcurrent { get; private set; }
        private readonly MemoryStream _data = new();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            lock (_lock) { _active++; if (_active > MaxConcurrent) MaxConcurrent = _active; }
            try
            {
                await Task.Delay(15, ct);
                lock (_lock) _data.Write(buffer.Span);
            }
            finally { lock (_lock) _active--; }
        }

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length { get { lock (_lock) return _data.Length; } }
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    // Counts WriteAsync invocations, to prove one frame goes out in exactly one write.
    private sealed class CountingStream : Stream
    {
        public int WriteAsyncCalls;
        private readonly MemoryStream _data = new();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Interlocked.Increment(ref WriteAsyncCalls);
            _data.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _data.Length;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Writer_SerialisesConcurrentWrites()
    {
        var stream = new YieldingStream();
        using var writer = new PipeMessageWriter(stream);
        Task a = writer.WriteAsync(new PipeMessage("log", "one"));
        Task b = writer.WriteAsync(new PipeMessage("log", "two"));
        await Task.WhenAll(a, b);
        Assert.Equal(1, stream.MaxConcurrent);
    }

    [Fact]
    public async Task DirectWrites_Interleave_WithoutTheWriter()
    {
        // Control test: the race the writer exists to prevent is real on this fake stream,
        // so the serialised test above is meaningful and not vacuously green.
        var stream = new YieldingStream();
        Task a = PipeProtocol.WriteAsync(stream, new PipeMessage("log", "one"));
        Task b = PipeProtocol.WriteAsync(stream, new PipeMessage("log", "two"));
        await Task.WhenAll(a, b);
        Assert.True(stream.MaxConcurrent >= 2);
    }

    [Fact]
    public async Task Frame_IsWrittenInOneCall()
    {
        var stream = new CountingStream();
        await PipeProtocol.WriteAsync(stream, new PipeMessage("exit", "0"));
        Assert.Equal(1, stream.WriteAsyncCalls);
    }

    [Fact]
    public async Task Frame_RoundTrips()
    {
        var buffer = new MemoryStream();
        await PipeProtocol.WriteAsync(buffer, new PipeMessage("exit", "0xC000013A"));
        buffer.Position = 0;
        PipeMessage? read = await PipeProtocol.ReadPipeMessageAsync(buffer);
        Assert.NotNull(read);
        Assert.Equal("exit", read!.Type);
        Assert.Equal("0xC000013A", read.Content);
    }
}
