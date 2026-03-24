using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;
using System.Text;

namespace MarkItDown.Core.Converters;

/// <summary>
/// Converts MOBI / AZW ebook files to Markdown.
/// Parses the PalmDB container, decompresses PalmDoc records, and converts
/// the extracted HTML content using <see cref="HtmlMarkdownConverter"/>.
/// </summary>
public sealed class MobiConverter : DocumentConverter
{
    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        if (streamInfo.Extension is ".mobi" or ".azw")
            return true;

        if (streamInfo.MimeType is not null &&
            (string.Equals(streamInfo.MimeType, "application/x-mobipocket-ebook", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(streamInfo.MimeType, "application/x-mobi8-ebook", StringComparison.OrdinalIgnoreCase)))
            return true;

        return HasMobiMagic(stream);
    }

    public override async Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        var data = await ReadAllBytesAsync(stream, cancellationToken);
        var (htmlContent, title) = ParseMobi(data);
        var markdown = HtmlMarkdownConverter.Convert(htmlContent).Markdown;
        return new DocumentConverterResult(markdown, title);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool HasMobiMagic(Stream stream)
    {
        if (stream.Length < 68) return false;

        stream.Position = 60;
        Span<byte> magic = stackalloc byte[8];
        return stream.Read(magic) == 8
            && magic[0] == 'B' && magic[1] == 'O' && magic[2] == 'O' && magic[3] == 'K'
            && magic[4] == 'M' && magic[5] == 'O' && magic[6] == 'B' && magic[7] == 'I';
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream is MemoryStream ms) return ms.ToArray();

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    // ── MOBI binary parser ────────────────────────────────────────────────────

    private static (string HtmlContent, string? Title) ParseMobi(byte[] data)
    {
        if (data.Length < 78)
            throw new InvalidOperationException("File is too small to be a valid MOBI file.");

        // PalmDB database name [0-31]: null-terminated, ASCII, used as title fallback.
        var nullIdx = Array.IndexOf(data, (byte)0, 0, 32);
        var palmTitle = nullIdx > 0
            ? Encoding.ASCII.GetString(data, 0, nullIdx)
            : Encoding.ASCII.GetString(data, 0, 32).TrimEnd('\0');

        // NumRecords [76-77] (big-endian)
        var numRecords = ReadUInt16(data, 76);
        if (numRecords < 2)
            throw new InvalidOperationException("MOBI file must have at least 2 records.");

        // Record 0 offset from record list [78-81]
        var record0Offset = (int)ReadUInt32(data, 78);

        // PalmDOC header inside record 0
        if (data.Length < record0Offset + 16)
            throw new InvalidOperationException("Record 0 is truncated.");

        var compression = ReadUInt16(data, record0Offset);       // +0
        var textLength   = (int)ReadUInt32(data, record0Offset + 4); // +4
        var textRecCount = ReadUInt16(data, record0Offset + 8);  // +8

        // MOBI header starts at record0Offset + 16
        var mobiHeaderStart = record0Offset + 16;
        string? mobiTitle = null;
        var encoding = Encoding.UTF8;

        if (data.Length >= mobiHeaderStart + 4
            && data[mobiHeaderStart]     == 'M'
            && data[mobiHeaderStart + 1] == 'O'
            && data[mobiHeaderStart + 2] == 'B'
            && data[mobiHeaderStart + 3] == 'I')
        {
            // TextEncoding at MOBI header +12 → record0Offset + 28
            if (data.Length >= record0Offset + 32)
            {
                var textEncoding = (int)ReadUInt32(data, record0Offset + 28);
                if (textEncoding == 1252)
                    encoding = TryGetEncoding(1252) ?? Encoding.Latin1;
            }

            // FullNameOffset/Length at MOBI header +68/+72 → record0Offset + 84/+88
            if (data.Length >= record0Offset + 92)
            {
                var nameOff = (int)ReadUInt32(data, record0Offset + 84);
                var nameLen = (int)ReadUInt32(data, record0Offset + 88);
                var nameAbsOffset = record0Offset + nameOff;

                if (nameLen > 0 && nameAbsOffset >= 0 && nameAbsOffset + nameLen <= data.Length)
                    mobiTitle = encoding.GetString(data, nameAbsOffset, nameLen);
            }
        }

        var title = !string.IsNullOrWhiteSpace(mobiTitle) ? mobiTitle
                  : !string.IsNullOrWhiteSpace(palmTitle)  ? palmTitle
                  : null;

        // Read text records (1 through textRecCount).
        var htmlParts = new List<string>(textRecCount);
        for (var i = 1; i <= textRecCount && i < numRecords; i++)
        {
            var recOffset   = (int)ReadUInt32(data, 78 + i * 8);
            var nextOffset  = (i < numRecords - 1)
                ? (int)ReadUInt32(data, 78 + (i + 1) * 8)
                : data.Length;

            nextOffset = Math.Min(nextOffset, data.Length);
            if (recOffset >= data.Length) continue;

            var recLen = nextOffset - recOffset;
            if (recLen <= 0) continue;

            var recordSpan = data.AsSpan(recOffset, recLen);

            // For PalmDoc (type 2), the last byte encodes how many bytes
            // at the end of the record are "trailing multibyte" bytes.
            // Strip them before decompression to avoid garbage output.
            if (compression == 2 && recordSpan.Length > 0)
            {
                var trailing = recordSpan[^1] & 0x03;
                recordSpan = recordSpan[..^(trailing + 1)];
            }

            var decoded = PalmDocDecoder.Decode(recordSpan, compression);
            htmlParts.Add(encoding.GetString(decoded));
        }

        // Clamp content to stated text length (decompressed bytes may overrun slightly).
        var html = string.Concat(htmlParts);
        if (html.Length > textLength && textLength > 0)
            html = html[..textLength];

        return (html, title);
    }

    private static Encoding? TryGetEncoding(int codePage)
    {
        try { return Encoding.GetEncoding(codePage); }
        catch { return null; }
    }

    // Big-endian readers
    private static ushort ReadUInt16(byte[] data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24)
        | ((uint)data[offset + 1] << 16)
        | ((uint)data[offset + 2] << 8)
        | data[offset + 3];
}
