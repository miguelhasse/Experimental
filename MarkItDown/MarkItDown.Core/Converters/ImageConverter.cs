using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;
using MetadataExtractor;
using Microsoft.Extensions.AI;

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

    public override async Task<DocumentConverterResult> ConvertAsync(
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

        if (context.LlmClient is IChatClient llmClient)
        {
            stream.Position = 0;
            var imageBytes = await StreamHelpers.ReadAllBytesAsync(stream, cancellationToken);
            var mimeType = streamInfo.MimeType ?? InferMimeType(streamInfo.Extension);

            var caption = await LlmHelpers.CaptionImageAsync(
                llmClient, imageBytes, mimeType,
                context.LlmModel, context.LlmPrompt, cancellationToken);

            if (caption is not null)
            {
                lines.Add("# Description:");
                lines.Add(caption.Trim());
                lines.Add(string.Empty);
            }
        }

        return new DocumentConverterResult(string.Join(Environment.NewLine, lines).Trim());
    }

    private static string InferMimeType(string? extension) => extension?.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png"            => "image/png",
        ".gif"            => "image/gif",
        ".bmp"            => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".webp"           => "image/webp",
        _                 => "image/jpeg",
    };
}

