using MarkItDown.Core.Models;
using System.Text;

namespace MarkItDown.Core.Utilities;

internal static class StreamHelpers
{
    private static readonly Encoding[] CandidateEncodings;

    static StreamHelpers()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        CandidateEncodings =
        [
            Encoding.GetEncoding(932),   // Shift-JIS / CP932 (Japanese)
            Encoding.GetEncoding(936),   // GBK (Simplified Chinese)
            Encoding.GetEncoding(950),   // Big5 (Traditional Chinese)
            Encoding.GetEncoding(949),   // EUC-KR (Korean)
            Encoding.GetEncoding(51932), // EUC-JP
            Encoding.Latin1,             // fallback
        ];
    }

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
            var declared = Encoding.GetEncoding(charset);
            // Validate the declared charset against the bytes. If the bytes are not
            // actually valid in that encoding (e.g. UTF-8 inferred by MimeHelpers for
            // a Shift-JIS CSV), fall through to the heuristic so we can pick correctly.
            var strictDeclared = Encoding.GetEncoding(
                declared.CodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            try
            {
                _ = strictDeclared.GetString(bytes);
                return declared;
            }
            catch (DecoderFallbackException)
            {
                // Declared charset cannot decode these bytes; fall through to heuristic.
            }
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
            // UTF-8 failed; score candidate encodings by round-trip fidelity.
        }

        return PickBestCandidateEncoding(bytes);
    }

    private static Encoding PickBestCandidateEncoding(byte[] bytes)
    {
        var bestEncoding = Encoding.Latin1;
        var bestScore = int.MaxValue;

        foreach (var candidate in CandidateEncodings)
        {
            try
            {
                var score = CountRoundTripMismatches(candidate, bytes);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestEncoding = candidate;
                    if (score == 0)
                        break; // Perfect match; CandidateEncodings is ordered by preference
                }
            }
            catch
            {
                // Encoding cannot decode these bytes; skip.
            }
        }

        return bestEncoding;
    }

    private static int CountRoundTripMismatches(Encoding encoding, byte[] bytes)
    {
        var decoded = encoding.GetString(bytes);
        var reencoded = encoding.GetBytes(decoded);

        var minLen = Math.Min(bytes.Length, reencoded.Length);
        var mismatches = Math.Abs(bytes.Length - reencoded.Length);

        for (var i = 0; i < minLen; i++)
        {
            if (bytes[i] != reencoded[i])
                mismatches++;
        }

        return mismatches;
    }
}
