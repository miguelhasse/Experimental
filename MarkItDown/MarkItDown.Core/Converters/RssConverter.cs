using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;
using System.ServiceModel.Syndication;
using System.Xml;

namespace MarkItDown.Core.Converters;

public sealed class RssConverter : DocumentConverter
{
    private static readonly string[] PreciseMimeTypePrefixes =
    [
        "application/rss",
        "application/rss+xml",
        "application/atom",
        "application/atom+xml"
    ];

    private static readonly string[] PreciseFileExtensions =
    [
        ".rss",
        ".atom"
    ];

    private static readonly string[] CandidateMimeTypePrefixes =
    [
        "text/xml",
        "application/xml"
    ];

    private static readonly string[] CandidateFileExtensions =
    [
        ".xml"
    ];

    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        var mimeType = streamInfo.MimeType ?? string.Empty;
        var extension = streamInfo.Extension ?? string.Empty;

        if (PreciseFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (PreciseMimeTypePrefixes.Any(prefix => mimeType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (CandidateFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            || CandidateMimeTypePrefixes.Any(prefix => mimeType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return IsFeedXml(stream);
        }

        return false;
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

        var feedLink = feed.Links.FirstOrDefault(l => string.Equals(l.RelationshipType, "alternate", StringComparison.OrdinalIgnoreCase))?.Uri
            ?? feed.Links.FirstOrDefault()?.Uri;
        if (feedLink is not null)
        {
            var feedTitle = feed.Title?.Text;
            lines.Add(string.IsNullOrWhiteSpace(feedTitle)
                ? feedLink.ToString()
                : $"[{feedTitle}]({feedLink})");
        }

        foreach (var item in feed.Items)
        {
            var itemTitle = item.Title?.Text ?? "Untitled";
            lines.Add($"## {itemTitle}");

            if (item.Links.FirstOrDefault()?.Uri is { } link)
            {
                lines.Add($"[{itemTitle}]({link})");
            }

            if (item.PublishDate != DateTimeOffset.MinValue)
            {
                lines.Add($"Published on: {item.PublishDate:R}");
            }

            var summary = item.Summary?.Text
                ?? (item.Content is TextSyndicationContent textContent ? textContent.Text : null);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                lines.Add(HtmlMarkdownConverter.Convert(summary).Markdown);
            }
        }

        return Task.FromResult(new DocumentConverterResult(string.Join(Environment.NewLine + Environment.NewLine, lines), feed.Title?.Text));
    }

    private static bool IsFeedXml(Stream stream)
    {
        var position = stream.Position;

        try
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = false, DtdProcessing = DtdProcessing.Prohibit });
            while (reader.Read() && reader.NodeType != XmlNodeType.Element)
            {
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                return false;
            }

            return reader.LocalName switch
            {
                "rss" => true,
                "feed" when string.Equals(reader.NamespaceURI, "http://www.w3.org/2005/Atom", StringComparison.Ordinal) => true,
                "rdf" when reader.ReadSubtree().ReadToFollowing("channel", "http://purl.org/rss/1.0/") => true,
                _ => false
            };
        }
        catch (XmlException)
        {
            return false;
        }
        finally
        {
            stream.Position = position;
        }
    }
}
