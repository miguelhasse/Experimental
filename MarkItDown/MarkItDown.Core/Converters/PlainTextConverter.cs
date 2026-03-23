using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;

namespace MarkItDown.Core.Converters;

public sealed class PlainTextConverter : DocumentConverter
{
    private static readonly string[] Extensions =
    [
        ".txt", ".text", ".md", ".markdown", ".json", ".jsonl", ".xml"
    ];

    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        if (Extensions.Contains(streamInfo.Extension, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (streamInfo.MimeType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true
            || string.Equals(streamInfo.MimeType, "application/json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(streamInfo.MimeType, "application/markdown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(streamInfo.MimeType, "application/xml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Accept if charset is set AND the MIME type is not a known binary type.
        return !string.IsNullOrWhiteSpace(streamInfo.Charset)
            && streamInfo.MimeType is null or { Length: > 0 } // only if MIME unknown or text-like
            && !IsBinaryMime(streamInfo.MimeType);
    }

    private static bool IsBinaryMime(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return false;
        }

        return mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || mimeType.StartsWith("application/octet", StringComparison.OrdinalIgnoreCase)
            || mimeType.StartsWith("application/zip", StringComparison.OrdinalIgnoreCase)
            || mimeType.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase)
            || mimeType.Contains("officedocument", StringComparison.OrdinalIgnoreCase)
            || mimeType.StartsWith("application/epub", StringComparison.OrdinalIgnoreCase);
    }

    public override async Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        var content = await StreamHelpers.ReadAllTextAsync(stream, streamInfo, cancellationToken);
        return new DocumentConverterResult(content);
    }
}
