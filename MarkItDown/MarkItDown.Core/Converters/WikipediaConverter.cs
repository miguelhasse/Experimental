using System.Text.RegularExpressions;
using HtmlAgilityPack;
using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;

namespace MarkItDown.Core.Converters;

public sealed partial class WikipediaConverter : DocumentConverter
{
    [GeneratedRegex(@"^https?://[a-zA-Z]{2,3}\.wikipedia\.org/", RegexOptions.IgnoreCase)]
    private static partial Regex WikipediaUrlRegex();

    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        return streamInfo.Url is not null && WikipediaUrlRegex().IsMatch(streamInfo.Url);
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

        var contentNode = document.DocumentNode.SelectSingleNode("//div[@id='mw-content-text']");
        if (contentNode is null)
        {
            return HtmlMarkdownConverter.Convert(html);
        }

        foreach (var tag in new[] { "script", "style" })
        {
            var nodes = contentNode.SelectNodes($".//{tag}");
            if (nodes is not null)
            {
                foreach (var node in nodes)
                    node.Remove();
            }
        }

        var titleSpan = document.DocumentNode.SelectSingleNode("//span[contains(@class,'mw-page-title-main')]");
        var rawTitle = titleSpan is not null
            ? titleSpan.InnerText.Trim()
            : document.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim() ?? string.Empty;

        var title = string.IsNullOrWhiteSpace(rawTitle) ? null : HtmlEntity.DeEntitize(rawTitle);

        var contentResult = HtmlMarkdownConverter.Convert(contentNode.OuterHtml);
        var markdown = title is not null
            ? $"# {title}\n\n{contentResult.Markdown}"
            : contentResult.Markdown;

        return new DocumentConverterResult(markdown, title);
    }
}
