using MarkItDown.Core.Models;
using MetadataExtractor;

namespace MarkItDown.Core.Converters;

public sealed class AudioConverter : DocumentConverter
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".m4a", ".mp4"
    };

    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        if (streamInfo.Extension is not null && Extensions.Contains(streamInfo.Extension))
            return true;

        var mime = streamInfo.MimeType;
        return mime is not null
            && (mime.StartsWith("audio/x-wav", StringComparison.OrdinalIgnoreCase)
             || mime.StartsWith("audio/mpeg", StringComparison.OrdinalIgnoreCase)
             || mime.StartsWith("video/mp4", StringComparison.OrdinalIgnoreCase)
             || mime.StartsWith("audio/mp4", StringComparison.OrdinalIgnoreCase)
             || mime.StartsWith("audio/m4a", StringComparison.OrdinalIgnoreCase));
    }

    public override Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MetadataExtractor.Directory> metadata;
        try
        {
            metadata = ImageMetadataReader.ReadMetadata(stream, streamInfo.FileName ?? "audio");
        }
        catch
        {
            return Task.FromResult(new DocumentConverterResult("_No metadata found._"));
        }

        var lines = new List<string>();

        foreach (var directory in metadata)
        {
            foreach (var tag in directory.Tags)
            {
                if (string.IsNullOrWhiteSpace(tag.Description))
                    continue;
                lines.Add($"{tag.Name}: {tag.Description}");
            }
        }

        var markdown = lines.Count > 0
            ? string.Join("\n", lines)
            : "_No metadata found._";

        return Task.FromResult(new DocumentConverterResult(markdown));
    }
}
