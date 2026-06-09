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
        byte[] len = BitConverter.GetBytes(json.Length);
        await stream.WriteAsync(len, ct);
        await stream.WriteAsync(json, ct);
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
