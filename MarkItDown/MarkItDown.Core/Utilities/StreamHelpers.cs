using MarkItDown.Core.Models;
using System.Text;

namespace MarkItDown.Core.Utilities;

internal static class StreamHelpers
{
    public static async Task<MemoryStream> BufferAsync(Stream stream, CancellationToken cancellationToken)
    {
        var output = new MemoryStream();

        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        await stream.CopyToAsync(output, cancellationToken);
        output.Position = 0;
        return output;
    }

    public static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffered = await BufferAsync(stream, cancellationToken);
        return buffered.ToArray();
    }

    public static async Task<string> ReadAllTextAsync(Stream stream, StreamInfo streamInfo, CancellationToken cancellationToken)
    {
        var bytes = await ReadAllBytesAsync(stream, cancellationToken);
        var encoding = GetEncoding(streamInfo.Charset, bytes);
        return encoding.GetString(bytes);
    }

    public static Encoding GetEncoding(string? charset, byte[] bytes)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.GetEncoding(charset);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8;
        }

        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode;
            }

            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode;
            }
        }

        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            _ = strictUtf8.GetString(bytes);
            return Encoding.UTF8;
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1;
        }
    }
}
