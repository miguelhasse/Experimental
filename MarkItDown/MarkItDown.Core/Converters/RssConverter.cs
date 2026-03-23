using MarkItDown.Core.Models;
using System.ServiceModel.Syndication;
using System.Xml;

namespace MarkItDown.Core.Converters;

public sealed class RssConverter : DocumentConverter
{
    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        return streamInfo.MimeType?.Contains("rss", StringComparison.OrdinalIgnoreCase) == true
            || streamInfo.MimeType?.Contains("atom", StringComparison.OrdinalIgnoreCase) == true
            || streamInfo.Extension is ".rss" or ".atom";
    }

    public override Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = false });
        var feed = SyndicationFeed.Load(reader);
        if (feed is null)
        {
            return Task.FromResult(new DocumentConverterResult(string.Empty));
        }

        var lines = new List<string>
        {
            $"# {feed.Title?.Text ?? streamInfo.Url ?? "Feed"}"
        };

        if (!string.IsNullOrWhiteSpace(feed.Description?.Text))
        {
            lines.Add(feed.Description.Text);
        }

        foreach (var item in feed.Items)
        {
            lines.Add($"## {item.Title?.Text ?? "Untitled"}");
            if (item.Links.FirstOrDefault()?.Uri is { } link)
            {
                lines.Add(link.ToString());
            }

            var summary = item.Summary?.Text
                ?? (item.Content is TextSyndicationContent textContent ? textContent.Text : null);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                lines.Add(summary);
            }
        }

        return Task.FromResult(new DocumentConverterResult(string.Join(Environment.NewLine + Environment.NewLine, lines), feed.Title?.Text));
    }
}
