using HtmlAgilityPack;
using MarkItDown.Core.Models;
using ReverseMarkdown;

namespace MarkItDown.Core.Utilities;

internal static class HtmlMarkdownConverter
{
    public static DocumentConverterResult Convert(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        RemoveNodes(document, "//script");
        RemoveNodes(document, "//style");

        var title = HtmlEntity.DeEntitize(document.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim() ?? string.Empty);
        var body = document.DocumentNode.SelectSingleNode("//body") ?? document.DocumentNode;

        var converter = new Converter(new Config
        {
            UnknownTags = Config.UnknownTagsOption.Bypass
        });

        var markdown = converter.Convert(body.OuterHtml).Trim();
        return new DocumentConverterResult(markdown, string.IsNullOrWhiteSpace(title) ? null : title);
    }

    private static void RemoveNodes(HtmlDocument document, string xpath)
    {
        var nodes = document.DocumentNode.SelectNodes(xpath);
        if (nodes is null)
        {
            return;
        }

        foreach (var node in nodes)
        {
            node.Remove();
        }
    }
}
