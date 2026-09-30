using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RunAsHelper.Shared.Protocol;

/// <summary>
/// Wire format: 4-byte LE length prefix followed by UTF-8 JSON body.
/// All messages are one of <see cref="LaunchRequest"/> or <see cref="PipeMessage"/>.
/// </summary>
public static class PipeProtocol
{
    public static async Task WriteAsync(Stream stream, LaunchRequest msg, CancellationToken ct = default)
        => await WriteFrameAsync(stream,
            JsonSerializer.SerializeToUtf8Bytes(msg, PipeJsonContext.Default.LaunchRequest), ct);

    public static async Task WriteAsync(Stream stream, PipeMessage msg, CancellationToken ct = default)
        => await WriteFrameAsync(stream,
            JsonSerializer.SerializeToUtf8Bytes(msg, PipeJsonContext.Default.PipeMessage), ct);

    public static async Task<LaunchRequest?> ReadLaunchRequestAsync(Stream stream, CancellationToken ct = default)
    {
        byte[]? frame = await ReadFrameAsync(stream, ct);
        return frame is null ? null : JsonSerializer.Deserialize(frame, PipeJsonContext.Default.LaunchRequest);
    }

    public static async Task<PipeMessage?> ReadPipeMessageAsync(Stream stream, CancellationToken ct = default)
    {
        byte[]? frame = await ReadFrameAsync(stream, ct);
        return frame is null ? null : JsonSerializer.Deserialize(frame, PipeJsonContext.Default.PipeMessage);
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] json, CancellationToken ct)
    {
        // Length prefix and body go out in ONE WriteAsync. Two writes could be observed
        // as a torn frame by a reader that wakes between them, and the previous split also
        // left a window where a second concurrent writer's length prefix could land between
        // this frame's prefix and body. One buffer, one write, closes both.
        byte[] frame = new byte[4 + json.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] lenBuf = new byte[4];
        try { await stream.ReadExactlyAsync(lenBuf, ct); }
        catch (EndOfStreamException) { return null; }

        int length = BitConverter.ToInt32(lenBuf);
        if (length <= 0 || length > 4 * 1024 * 1024) return null;

        byte[] body = new byte[length];
        try { await stream.ReadExactlyAsync(body, ct); }
        catch (EndOfStreamException) { return null; }

        return body;
    }
}

/// <summary>
/// One writer per pipe connection. Every frame the service sends to a client goes
/// through this so a frame written from a background task (the stdout pump) can never
/// interleave with one written from the request handler (the exit or timeout line).
/// A single frame is already atomic on the wire (WriteFrameAsync issues one WriteAsync);
/// this serialises the two producers so their frames also stay whole relative to each other.
/// </summary>
public sealed class PipeMessageWriter(Stream stream) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task WriteAsync(PipeMessage msg, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await PipeProtocol.WriteAsync(stream, msg, ct); }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
