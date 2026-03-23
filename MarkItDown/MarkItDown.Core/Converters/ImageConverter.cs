using MarkItDown.Core.Models;
using MetadataExtractor;

namespace MarkItDown.Core.Converters;

public sealed class ImageConverter : DocumentConverter
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp"
    };

    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        return (streamInfo.Extension is not null && Extensions.Contains(streamInfo.Extension))
            || streamInfo.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
    }

    public override Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        var metadata = ImageMetadataReader.ReadMetadata(stream, streamInfo.FileName ?? "image");
        var lines = new List<string>();

        foreach (var directory in metadata)
        {
            var tags = directory.Tags.ToArray();
            if (tags.Length == 0)
            {
                continue;
            }

            lines.Add($"## {directory.Name}");
            foreach (var tag in tags)
            {
                lines.Add($"- {tag.Name}: {tag.Description}");
            }
            lines.Add(string.Empty);
        }

        return Task.FromResult(new DocumentConverterResult(string.Join(Environment.NewLine, lines).Trim()));
    }
}
