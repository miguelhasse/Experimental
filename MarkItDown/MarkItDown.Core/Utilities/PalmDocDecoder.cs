namespace MarkItDown.Core.Utilities;

/// <summary>
/// Decodes PalmDoc-compressed content from MOBI/AZW ebook records.
/// Supports compression type 1 (none) and type 2 (PalmDoc LZ77).
/// </summary>
internal static class PalmDocDecoder
{
    /// <summary>
    /// Decodes a single PalmDoc record.
    /// </summary>
    /// <param name="record">The raw record bytes (trailing size byte already stripped).</param>
    /// <param name="compressionType">1 = no compression, 2 = PalmDoc LZ77.</param>
    public static byte[] Decode(ReadOnlySpan<byte> record, int compressionType)
    {
        if (compressionType == 1)
            return record.ToArray();

        if (compressionType != 2)
            throw new NotSupportedException(
                $"Unsupported PalmDoc compression type {compressionType}. Only types 1 (none) and 2 (LZ77) are supported.");

        return DecodeLz77(record);
    }

    // PalmDoc LZ77 decompression rules:
    //   0x00        → literal 0x00
    //   0x01-0x08   → next <byte> bytes are uncompressed literals
    //   0x09-0x7F   → literal byte
    //   0x80-0xBF   → two-byte back-reference: copy from output window
    //   0xC0-0xFF   → space (0x20) + ASCII char (byte & 0x7F)
    private static byte[] DecodeLz77(ReadOnlySpan<byte> input)
    {
        // Pre-allocate with a 2× heuristic; List<byte> handles overflow automatically.
        var output = new List<byte>(input.Length * 2);
        var i = 0;

        while (i < input.Length)
        {
            var b = input[i++];

            if (b == 0x00)
            {
                output.Add(0x00);
            }
            else if (b <= 0x08)
            {
                // Next `b` bytes are raw literals.
                var count = Math.Min(b, input.Length - i);
                for (var j = 0; j < count; j++)
                    output.Add(input[i++]);
            }
            else if (b <= 0x7F)
            {
                output.Add(b);
            }
            else if (b <= 0xBF)
            {
                // Two-byte back-reference.
                if (i >= input.Length) break;
                var b2 = input[i++];
                var distance = ((b & 0x3F) << 3) | (b2 >> 5);
                var length = (b2 & 0x07) + 3;

                if (distance == 0) continue;

                var start = output.Count - distance;
                for (var j = 0; j < length; j++)
                {
                    var srcIdx = start + j;
                    output.Add(srcIdx >= 0 ? output[srcIdx] : (byte)0x20);
                }
            }
            else
            {
                // 0xC0-0xFF: space followed by printable ASCII.
                output.Add(0x20);
                output.Add((byte)(b & 0x7F));
            }
        }

        return [.. output];
    }
}
