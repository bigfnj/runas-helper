using System;
using System.Text;

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
