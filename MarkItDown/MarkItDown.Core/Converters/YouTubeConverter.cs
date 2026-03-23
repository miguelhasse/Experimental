using HtmlAgilityPack;
using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;

namespace MarkItDown.Core.Converters;

public sealed class YouTubeConverter : DocumentConverter
{
    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        if (streamInfo.Url is null || !Uri.TryCreate(streamInfo.Url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
    }

    public override async Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        var html = await StreamHelpers.ReadAllTextAsync(stream, streamInfo, cancellationToken);
        var document = new HtmlDocument();
        document.LoadHtml(html);

        static string? SelectMeta(HtmlDocument doc, string attr, string value)
        {
            var content = doc.DocumentNode
                .SelectSingleNode($"//meta[@{attr}='{value}']")
                ?.GetAttributeValue("content", string.Empty);
            return string.IsNullOrWhiteSpace(content) ? null : content;
        }

        var title = SelectMeta(document, "property", "og:title")
            ?? SelectMeta(document, "name", "title")
            ?? document.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim();

        var description = SelectMeta(document, "property", "og:description")
            ?? SelectMeta(document, "name", "description");

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(title))
        {
            lines.Add($"# {title}");
        }

        if (!string.IsNullOrWhiteSpace(streamInfo.Url))
        {
            lines.Add(streamInfo.Url);
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            lines.Add(description);
        }

        return new DocumentConverterResult(string.Join(Environment.NewLine + Environment.NewLine, lines), title);
    }
}
