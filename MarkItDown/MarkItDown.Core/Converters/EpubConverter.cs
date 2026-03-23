using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;
using VersOne.Epub;

namespace MarkItDown.Core.Converters;

public sealed class EpubConverter : DocumentConverter
{
    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        return streamInfo.Extension is ".epub"
            || string.Equals(streamInfo.MimeType, "application/epub+zip", StringComparison.OrdinalIgnoreCase);
    }

    public override async Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        var book = await EpubReader.ReadBookAsync(stream);
        var sections = new List<string>();

        foreach (var chapter in book.ReadingOrder)
        {
            if (string.IsNullOrWhiteSpace(chapter.Content))
            {
                continue;
            }

            var markdown = HtmlMarkdownConverter.Convert(chapter.Content).Markdown;
            sections.Add($"## {chapter.Key}{Environment.NewLine}{Environment.NewLine}{markdown}");
        }

        return new DocumentConverterResult(string.Join(Environment.NewLine + Environment.NewLine, sections), book.Title);
    }
}
