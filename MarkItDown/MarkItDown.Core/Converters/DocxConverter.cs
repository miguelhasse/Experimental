using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;

namespace MarkItDown.Core.Converters;

public sealed class DocxConverter : DocumentConverter
{
    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        return streamInfo.Extension is ".docx"
            || string.Equals(
                streamInfo.MimeType,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                StringComparison.OrdinalIgnoreCase);
    }

    public override Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        using var document = WordprocessingDocument.Open(stream, false);
        var title = document.PackageProperties.Title;
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return Task.FromResult(new DocumentConverterResult(string.Empty, title));
        }

        var blocks = new List<string>();
        foreach (var element in body.Elements())
        {
            switch (element)
            {
                case Paragraph paragraph:
                    {
                        var text = NormalizeWhitespace(paragraph.InnerText);
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            continue;
                        }

                        var headingLevel = TryGetHeadingLevel(paragraph);
                        if (headingLevel is not null)
                        {
                            blocks.Add($"{new string('#', headingLevel.Value)} {text}");
                            continue;
                        }

                        if (paragraph.ParagraphProperties?.NumberingProperties is not null)
                        {
                            blocks.Add($"- {text}");
                            continue;
                        }

                        blocks.Add(text);
                        break;
                    }

                case Table table:
                    var rows = table.Elements<TableRow>()
                        .Select(row => (IReadOnlyList<string?>)row.Elements<TableCell>()
                            .Select(cell => NormalizeWhitespace(cell.InnerText))
                            .ToArray())
                        .ToArray();
                    var markdownTable = MarkdownHelpers.BuildTable(rows);
                    if (!string.IsNullOrWhiteSpace(markdownTable))
                    {
                        blocks.Add(markdownTable);
                    }
                    break;
            }
        }

        return Task.FromResult(new DocumentConverterResult(string.Join(Environment.NewLine + Environment.NewLine, blocks), title));
    }

    private static int? TryGetHeadingLevel(Paragraph paragraph)
    {
        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (styleId is null || !styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(styleId["Heading".Length..], out var headingLevel)
            ? Math.Clamp(headingLevel, 1, 6)
            : null;
    }

    private static string NormalizeWhitespace(string value)
    {
        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
