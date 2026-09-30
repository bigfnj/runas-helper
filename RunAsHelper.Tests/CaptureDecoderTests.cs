using System;
using System.Text;
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
