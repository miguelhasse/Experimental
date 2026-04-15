using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;
using Microsoft.Extensions.AI;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Presentation = DocumentFormat.OpenXml.Presentation;

namespace MarkItDown.Core.Converters;

public sealed partial class PptxConverter : DocumentConverter
{
    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        return streamInfo.Extension is ".pptx"
            || streamInfo.MimeType?.StartsWith("application/vnd.openxmlformats-officedocument.presentationml", StringComparison.OrdinalIgnoreCase) == true;
    }

    public override async Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        using var presentation = PresentationDocument.Open(stream, false);
        var presentationPart = presentation.PresentationPart;
        var slideIds = presentationPart?.Presentation?.SlideIdList?.Elements<Presentation.SlideId>().ToArray() ?? [];
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

            var state = new SlideState();
            await ProcessSlideContainerAsync(slidePart, slidePart.Slide.CommonSlideData?.ShapeTree, lines, state, context, cancellationToken);

            var notes = NormalizeWhitespace(slidePart.NotesSlidePart?.NotesSlide?.InnerText ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(notes))
            {
                lines.Add("### Notes:");
                lines.Add(notes);
            }

            sections.Add(string.Join(Environment.NewLine + Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line))));
        }

        return new DocumentConverterResult(string.Join(Environment.NewLine + Environment.NewLine, sections));
    }

    private static async Task ProcessSlideContainerAsync(
        SlidePart slidePart,
        OpenXmlElement? container,
        List<string> lines,
        SlideState state,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken)
    {
        if (container is null)
        {
            return;
        }

        // Sort shapes by visual position (top-to-bottom, left-to-right) to match reading order,
        // matching the behaviour of upstream python-pptx which sorts by (shape.top, shape.left).
        var orderedChildren = container.ChildElements
            .Select(child => (child, pos: GetShapePosition(child)))
            .OrderBy(t => t.pos.Y)
            .ThenBy(t => t.pos.X)
            .Select(t => t.child)
            .ToList();

        foreach (var child in orderedChildren)
        {
            if (child is Presentation.Shape shape)
            {
                var text = NormalizeWhitespace(shape.InnerText);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (!state.TitleWritten && IsTitlePlaceholder(shape))
                    {
                        lines.Add($"# {text}");
                        state.TitleWritten = true;
                    }
                    else
                    {
                        lines.Add(text);
                    }
                }

                continue;
            }

            if (child.LocalName == "pic")
            {
                var pictureMarkdown = await ConvertPictureToMarkdownAsync(child, slidePart, context, cancellationToken);
                if (!string.IsNullOrWhiteSpace(pictureMarkdown))
                {
                    lines.Add(pictureMarkdown);
                }

                continue;
            }

            if (child.LocalName == "graphicFrame")
            {
                var graphicMarkdown = ConvertGraphicFrameToMarkdown(child, slidePart);
                if (!string.IsNullOrWhiteSpace(graphicMarkdown))
                {
                    lines.Add(graphicMarkdown);
                }

                continue;
            }

            if (child is Presentation.GroupShape groupShape)
            {
                await ProcessSlideContainerAsync(slidePart, groupShape, lines, state, context, cancellationToken);
            }
        }
    }

    private static async Task<string?> ConvertPictureToMarkdownAsync(
        OpenXmlElement picture,
        SlidePart slidePart,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken)
    {
        var document = XDocument.Parse(picture.OuterXml);
        var drawingProperties = document.Descendants(PptNs + "cNvPr").FirstOrDefault();
        var altText = SanitizeAltText(
            drawingProperties?.Attribute("descr")?.Value
            ?? drawingProperties?.Attribute("name")?.Value
            ?? string.Empty);
        if (string.IsNullOrWhiteSpace(altText))
        {
            altText = "image";
        }

        var embedId = document.Descendants(ANs + "blip")
            .Attributes(RNs + "embed")
            .Select(attribute => attribute.Value)
            .FirstOrDefault();
        var imagePart = embedId is null ? null : slidePart.GetPartById(embedId) as ImagePart;
        var fileName = imagePart?.Uri is Uri uri
            ? Path.GetFileName(uri.OriginalString)
            : null;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"{NonWordRegex().Replace(altText, string.Empty)}.jpg";
        }

        if (context.LlmClient is IChatClient llmClient && imagePart is not null)
        {
            try
            {
                using var imageStream = imagePart.GetStream();
                var imageBytes = new byte[imageStream.Length];
                await imageStream.ReadExactlyAsync(imageBytes, cancellationToken);

                var mimeType = imagePart.ContentType ?? "image/jpeg";
                var caption = await LlmHelpers.CaptionImageAsync(
                    llmClient, imageBytes, mimeType,
                    context.LlmModel, context.LlmPrompt, cancellationToken);

                if (caption is not null)
                {
                    altText = string.IsNullOrWhiteSpace(altText) || altText == "image"
                        ? caption.Trim()
                        : $"{caption.Trim()} {altText}";
                }
            }
            catch
            {
                // Ignore; use existing altText.
            }
        }

        return $"![{altText}]({fileName})";
    }

    private static string? ConvertGraphicFrameToMarkdown(OpenXmlElement graphicFrame, SlidePart slidePart)
    {
        var document = XDocument.Parse(graphicFrame.OuterXml);

        var table = document.Descendants(ANs + "table").FirstOrDefault();
        if (table is not null)
        {
            var rows = table.Elements(ANs + "tr")
                .Select(row => (IReadOnlyList<string?>)row.Elements(ANs + "tc")
                    .Select(cell => NormalizeWhitespace(string.Join(" ", cell.Descendants(ANs + "t").Select(text => text.Value))))
                    .ToArray())
                .ToArray();

            return MarkdownHelpers.BuildTable(rows);
        }

        var chartReference = document.Descendants(CNs + "chart")
            .Attributes(RNs + "id")
            .Select(attribute => attribute.Value)
            .FirstOrDefault();
        var chartPart = chartReference is null ? null : slidePart.GetPartById(chartReference) as ChartPart;
        var chartXml = chartPart?.ChartSpace?.OuterXml;
        if (string.IsNullOrWhiteSpace(chartXml))
        {
            return null;
        }

        return ConvertChartXmlToMarkdown(XDocument.Parse(chartXml));
    }

    private static string? ConvertChartXmlToMarkdown(XDocument chartDocument)
    {
        var chart = chartDocument.Descendants(CNs + "chart").FirstOrDefault();
        if (chart is null)
        {
            return null;
        }

        var series = chart.Descendants(CNs + "ser").ToArray();
        if (series.Length == 0)
        {
            return null;
        }

        var categories = GetChartValues(series[0].Element(CNs + "cat"));
        if (categories.Count == 0)
        {
            return null;
        }

        var seriesNames = series.Select((item, index) => GetChartSeriesName(item) ?? $"Series {index + 1}").ToArray();
        var rows = new List<IReadOnlyList<string?>>();
        var header = new string?[series.Length + 1];
        header[0] = "Category";
        Array.Copy(seriesNames, 0, header, 1, seriesNames.Length);
        rows.Add(header);

        for (var rowIndex = 0; rowIndex < categories.Count; rowIndex++)
        {
            var row = new string?[series.Length + 1];
            row[0] = categories[rowIndex];
            for (var seriesIndex = 0; seriesIndex < series.Length; seriesIndex++)
            {
                var values = GetChartValues(series[seriesIndex].Element(CNs + "val"));
                row[seriesIndex + 1] = rowIndex < values.Count ? values[rowIndex] : string.Empty;
            }

            rows.Add(row);
        }

        var chartTitleParts = chart.Element(CNs + "title")?.Descendants(ANs + "t").Select(text => text.Value).ToArray() ?? [];
        var chartTitle = NormalizeWhitespace(string.Join(" ", chartTitleParts));
        var heading = string.IsNullOrWhiteSpace(chartTitle) ? "### Chart" : $"### Chart: {chartTitle}";
        var table = MarkdownHelpers.BuildTable(rows.ToArray());
        return string.IsNullOrWhiteSpace(table)
            ? heading
            : $"{heading}{Environment.NewLine}{Environment.NewLine}{table}";
    }

    private static IReadOnlyList<string> GetChartValues(XElement? valueContainer)
    {
        if (valueContainer is null)
        {
            return [];
        }

        var cache = valueContainer.Descendants(CNs + "strCache").FirstOrDefault()
            ?? valueContainer.Descendants(CNs + "numCache").FirstOrDefault()
            ?? valueContainer.Descendants(CNs + "multiLvlStrCache").FirstOrDefault();
        if (cache is null)
        {
            return [];
        }

        return cache.Elements(CNs + "pt")
            .OrderBy(point => (int?)point.Attribute("idx") ?? 0)
            .Select(point => NormalizeWhitespace(point.Element(CNs + "v")?.Value ?? string.Empty))
            .ToArray();
    }

    private static string? GetChartSeriesName(XElement series)
    {
        var name = series.Element(CNs + "tx")?.Descendants(CNs + "v").FirstOrDefault()?.Value
            ?? series.Element(CNs + "tx")?.Descendants(ANs + "t").FirstOrDefault()?.Value;
        return string.IsNullOrWhiteSpace(name) ? null : NormalizeWhitespace(name);
    }

    /// <summary>
    /// Returns the (Y, X) position of a shape element in EMUs by reading the
    /// <c>a:off</c> element's <c>y</c> and <c>x</c> attributes.  Falls back to
    /// <see cref="long.MaxValue"/> so un-positioned elements sort last.
    /// </summary>
    private static (long Y, long X) GetShapePosition(OpenXmlElement element)
    {
        var doc = XDocument.Parse(element.OuterXml);
        var off = doc.Descendants(ANs + "off").FirstOrDefault();
        if (off is null)
        {
            return (long.MaxValue, long.MaxValue);
        }

        var y = (long?)off.Attribute("y") ?? long.MaxValue;
        var x = (long?)off.Attribute("x") ?? long.MaxValue;
        return (y, x);
    }

    private static bool IsTitlePlaceholder(Presentation.Shape shape)
    {
        var placeholder = shape.NonVisualShapeProperties?
            .ApplicationNonVisualDrawingProperties?
            .GetFirstChild<Presentation.PlaceholderShape>();

        var value = placeholder?.Type?.Value;
        return value == Presentation.PlaceholderValues.Title || value == Presentation.PlaceholderValues.CenteredTitle;
    }

    private static string NormalizeWhitespace(string value)
    {
        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string SanitizeAltText(string value)
    {
        return NormalizeWhitespace(value.Replace('\r', ' ').Replace('\n', ' ').Replace('[', ' ').Replace(']', ' '));
    }

    [GeneratedRegex(@"\W+")]
    private static partial Regex NonWordRegex();

    private static readonly XNamespace PptNs = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace CNs = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private static readonly XNamespace RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private sealed class SlideState
    {
        public bool TitleWritten;
    }
}
