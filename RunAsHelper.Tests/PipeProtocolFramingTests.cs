using System;
using System.IO;
using System.Threading.Tasks;
using RunAsHelper.Shared.Protocol;
using Xunit;

namespace RunAsHelper.Tests;

/// <summary>
/// Framing contract for the length-prefixed pipe protocol: a written message reads
/// back byte-for-byte, and a declared length past the 4 MiB ceiling is rejected as
/// null rather than allocating an arbitrary buffer.
/// </summary>
public class PipeProtocolFramingTests
{
    [Fact]
    public async Task RoundTrip_PipeMessage_PreservesValue()
    {
        var msg = new PipeMessage("log", "Account=system launching cmd.exe with PSV marker");
        using var ms = new MemoryStream();

        await PipeProtocol.WriteAsync(ms, msg);
        ms.Position = 0;
        PipeMessage? read = await PipeProtocol.ReadPipeMessageAsync(ms);

        // PipeMessage is a record, so equality compares Type and Content by value.
        Assert.Equal(msg, read);
    }

    [Fact]
    public async Task RoundTrip_EmptyContent_PreservesValue()
    {
        var msg = new PipeMessage("exit", "");
        using var ms = new MemoryStream();

        await PipeProtocol.WriteAsync(ms, msg);
        ms.Position = 0;
        PipeMessage? read = await PipeProtocol.ReadPipeMessageAsync(ms);

        Assert.Equal(msg, read);
    }

    [Fact]
    public async Task OversizeLength_ReturnsNull()
    {
        using var ms = new MemoryStream();

        // A length one byte past the 4 MiB ceiling in PipeProtocol.ReadFrameAsync. The
        // reader must refuse it before it tries to allocate or read that many bytes.
        int oversize = (4 * 1024 * 1024) + 1;
        ms.Write(BitConverter.GetBytes(oversize));
        ms.Write(new byte[16]);
        ms.Position = 0;

        PipeMessage? read = await PipeProtocol.ReadPipeMessageAsync(ms);

        Assert.Null(read);
    }

    [Fact]
    public async Task NonPositiveLength_ReturnsNull()
    {
        using var ms = new MemoryStream();

        // A zero or negative length is also rejected, never read as an empty body.
        ms.Write(BitConverter.GetBytes(0));
        ms.Position = 0;

        PipeMessage? read = await PipeProtocol.ReadPipeMessageAsync(ms);

        Assert.Null(read);
    }
}
