using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MarkItDown.Core.Converters;

public sealed partial class DocxConverter : DocumentConverter
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
        var mainDocumentPart = document.MainDocumentPart;
        var body = mainDocumentPart?.Document?.Body;

        if (body is null)
        {
            return Task.FromResult(new DocumentConverterResult(string.Empty, title));
        }

        var styleCache = BuildStyleHeadingCache(mainDocumentPart);

        var blocks = new List<string>();
        foreach (var element in body.Elements())
        {
            switch (element)
            {
                case Paragraph paragraph:
                    {
                        var parts = GetParagraphParts(paragraph, mainDocumentPart);
                        if (parts.Count == 0)
                        {
                            continue;
                        }

                        var headingLevel = TryGetHeadingLevel(paragraph, styleCache);
                        if (headingLevel is not null)
                        {
                            var headingText = NormalizeWhitespace(string.Join(" ", parts.Where(part => !part.StartsWith("![", StringComparison.Ordinal))));
                            if (!string.IsNullOrWhiteSpace(headingText))
                            {
                                blocks.Add($"{new string('#', headingLevel.Value)} {headingText}");
                            }

                            continue;
                        }

                        var text = NormalizeWhitespace(string.Join(" ", parts));
                        if (string.IsNullOrWhiteSpace(text))
                        {
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
                    {
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
        }

        return Task.FromResult(new DocumentConverterResult(string.Join(Environment.NewLine + Environment.NewLine, blocks), title));
    }

    private static int? TryGetHeadingLevel(Paragraph paragraph, Dictionary<string, int?> styleCache)
    {
        var outlineLevel = paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value;
        if (outlineLevel is not null)
        {
            return Math.Clamp((int)outlineLevel.Value + 1, 1, 6);
        }

        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (styleId is null)
        {
            return null;
        }

        if (styleCache.TryGetValue(styleId, out var cachedLevel))
        {
            return cachedLevel;
        }

        // Fallback direct match when no styles part is present
        var match = HeadingStyleRegex().Match(styleId);
        return match.Success && int.TryParse(match.Groups[1].Value, out var headingLevel)
            ? Math.Clamp(headingLevel, 1, 6)
            : null;
    }

    private static Dictionary<string, int?> BuildStyleHeadingCache(MainDocumentPart? mainDocumentPart)
    {
        var cache = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        var styles = mainDocumentPart?.StyleDefinitionsPart?.Styles;
        if (styles is null)
        {
            return cache;
        }

        var parentOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var style in styles.Elements<Style>())
        {
            var id = style.StyleId?.Value;
            var parent = style.BasedOn?.Val?.Value;
            if (id is not null && parent is not null)
            {
                parentOf[id] = parent;
            }
        }

        foreach (var style in styles.Elements<Style>())
        {
            var startId = style.StyleId?.Value;
            if (startId is null || cache.ContainsKey(startId))
            {
                continue;
            }

            var chain = new List<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int? resolvedLevel = null;
            string? current = startId;

            while (current is not null && visited.Add(current))
            {
                if (cache.TryGetValue(current, out resolvedLevel))
                {
                    break;
                }

                chain.Add(current);

                var match = HeadingStyleRegex().Match(current);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var lvl))
                {
                    resolvedLevel = Math.Clamp(lvl, 1, 6);
                    break;
                }

                parentOf.TryGetValue(current, out current);
            }

            foreach (var id in chain)
            {
                cache[id] = resolvedLevel;
            }
        }

        return cache;
    }

    private static List<string> GetParagraphParts(Paragraph paragraph, MainDocumentPart? mainDocumentPart)
    {
        var parts = new List<string>();

        foreach (var run in paragraph.Descendants<Run>())
        {
            var rpr = run.RunProperties;
            var isBold = rpr?.Bold is not null && rpr.Bold.Val?.Value != false;
            var isItalic = rpr?.Italic is not null && rpr.Italic.Val?.Value != false;

            foreach (var child in run.ChildElements)
            {
                switch (child)
                {
                    case Text text when !string.IsNullOrWhiteSpace(text.Text):
                        var content = text.Text;
                        if (isBold && isItalic)
                            content = $"**_{content}_**";
                        else if (isBold)
                            content = $"**{content}**";
                        else if (isItalic)
                            content = $"_{content}_";
                        parts.Add(content);
                        break;
                    case TabChar:
                    case Break:
                        parts.Add(" ");
                        break;
                    case DocumentFormat.OpenXml.Wordprocessing.Drawing drawing:
                        {
                            var imageMarkdown = TryConvertDrawingToMarkdown(drawing, mainDocumentPart);
                            if (!string.IsNullOrWhiteSpace(imageMarkdown))
                            {
                                parts.Add(imageMarkdown);
                            }

                            break;
                        }
                }
            }
        }

        return parts;
    }

    private static string? TryConvertDrawingToMarkdown(DocumentFormat.OpenXml.Wordprocessing.Drawing drawing, MainDocumentPart? mainDocumentPart)
    {
        var document = XDocument.Parse(drawing.OuterXml);
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";

        var embedId = document
            .Descendants(a + "blip")
            .Attributes(r + "embed")
            .Select(attribute => attribute.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        if (embedId is null || mainDocumentPart is null)
        {
            return null;
        }

        if (mainDocumentPart.GetPartById(embedId) is not ImagePart imagePart)
        {
            return null;
        }

        var altText = document
            .Descendants(wp + "docPr")
            .Select(element => element.Attribute("descr")?.Value ?? element.Attribute("name")?.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? "image";

        using var imageStream = imagePart.GetStream();
        using var memoryStream = new MemoryStream();
        imageStream.CopyTo(memoryStream);

        return $"![{altText}]({BuildDataUri(imagePart.ContentType, memoryStream.ToArray())})";
    }

    private static string BuildDataUri(string contentType, byte[] bytes)
    {
        return $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string NormalizeWhitespace(string value)
    {
        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    [GeneratedRegex(@"^Heading\s*(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingStyleRegex();
}
