using DocumentFormat.OpenXml.Packaging;
using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace MarkItDown.Core.Converters;

public sealed class PptxConverter : DocumentConverter
{
    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        return streamInfo.Extension is ".pptx"
            || streamInfo.MimeType?.StartsWith("application/vnd.openxmlformats-officedocument.presentationml", StringComparison.OrdinalIgnoreCase) == true;
    }

    public override Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        using var presentation = PresentationDocument.Open(stream, false);
        var presentationPart = presentation.PresentationPart;
        var slideIds = presentationPart?.Presentation?.SlideIdList?.Elements<P.SlideId>().ToArray() ?? [];
        var sections = new List<string>();

        for (var index = 0; index < slideIds.Length; index++)
        {
            var slidePart = (SlidePart)presentationPart!.GetPartById(slideIds[index].RelationshipId!);
            if (slidePart.Slide is null)
            {
                continue;
            }

            var lines = new List<string>
            {
                $"<!-- Slide number: {index + 1} -->"
            };

            var titleWritten = false;
            foreach (var shape in slidePart.Slide.Descendants<P.Shape>())
            {
                var text = NormalizeWhitespace(shape.InnerText);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (!titleWritten && IsTitlePlaceholder(shape))
                {
                    lines.Add($"# {text}");
                    titleWritten = true;
                }
                else
                {
                    lines.Add(text);
                }
            }

            foreach (var table in slidePart.Slide.Descendants<A.Table>())
            {
                var rows = table.Elements<A.TableRow>()
                    .Select(row => (IReadOnlyList<string?>)row.Elements<A.TableCell>()
                        .Select(cell => NormalizeWhitespace(cell.InnerText))
                        .ToArray())
                    .ToArray();

                var markdownTable = MarkdownHelpers.BuildTable(rows);
                if (!string.IsNullOrWhiteSpace(markdownTable))
                {
                    lines.Add(markdownTable);
                }
            }

            var notes = NormalizeWhitespace(slidePart.NotesSlidePart?.NotesSlide?.InnerText ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(notes))
            {
                lines.Add("### Notes:");
                lines.Add(notes);
            }

            sections.Add(string.Join(Environment.NewLine + Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line))));
        }

        return Task.FromResult(new DocumentConverterResult(string.Join(Environment.NewLine + Environment.NewLine, sections)));
    }

    private static bool IsTitlePlaceholder(P.Shape shape)
    {
        var placeholder = shape.NonVisualShapeProperties?
            .ApplicationNonVisualDrawingProperties?
            .GetFirstChild<P.PlaceholderShape>();

        var value = placeholder?.Type?.Value;
        return value == P.PlaceholderValues.Title || value == P.PlaceholderValues.CenteredTitle;
    }

    private static string NormalizeWhitespace(string value)
    {
        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
