using HtmlAgilityPack;
using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MarkItDown.Core.Converters;

public sealed partial class YouTubeConverter : DocumentConverter
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

        // Extended metadata from structured page data (matches upstream Python behaviour).
        var views = SelectMeta(document, "itemprop", "interactionCount");
        var keywords = SelectMeta(document, "name", "keywords");
        var duration = SelectMeta(document, "itemprop", "duration");

        // Full description from ytInitialData shortDescription (more complete than og:description).
        var description = ExtractShortDescription(html)
            ?? SelectMeta(document, "property", "og:description")
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

        // Video Metadata section (only when at least one field is available).
        var metaLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(views))
        {
            metaLines.Add($"- **Views:** {views}");
        }

        if (!string.IsNullOrWhiteSpace(duration))
        {
            metaLines.Add($"- **Duration:** {duration}");
        }

        if (!string.IsNullOrWhiteSpace(keywords))
        {
            metaLines.Add($"- **Keywords:** {keywords}");
        }

        if (metaLines.Count > 0)
        {
            lines.Add($"## Video Metadata{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, metaLines)}");
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            lines.Add($"## Description{Environment.NewLine}{Environment.NewLine}{description}");
        }

        // Transcript section.
        var transcript = await FetchTranscriptAsync(html, context.HttpClient, cancellationToken);
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            lines.Add($"## Transcript{Environment.NewLine}{Environment.NewLine}{transcript}");
        }

        return new DocumentConverterResult(string.Join(Environment.NewLine + Environment.NewLine, lines), title);
    }

    /// <summary>
    /// Extracts the full <c>shortDescription</c> from the <c>ytInitialData</c> JSON embedded in
    /// the page HTML.  This is more complete than the truncated <c>og:description</c> meta tag.
    /// </summary>
    private static string? ExtractShortDescription(string html)
    {
        var match = ShortDescriptionRegex().Match(html);
        if (!match.Success)
        {
            return null;
        }

        var raw = match.Groups[1].Value;
        return UnescapeJsonString(raw);
    }

    /// <summary>
    /// Fetches the first available caption track from <c>ytInitialData</c> and returns the
    /// transcript as plain text.  Returns <c>null</c> when no transcript is available.
    /// </summary>
    private static async Task<string?> FetchTranscriptAsync(
        string html,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        try
        {
            var trackUrl = ExtractCaptionTrackUrl(html);
            if (trackUrl is null)
            {
                return null;
            }

            var xmlContent = await httpClient.GetStringAsync(trackUrl, cancellationToken);
            return ParseTranscriptXml(xmlContent);
        }
        catch
        {
            // Transcript is a best-effort feature; never fail the conversion.
            return null;
        }
    }

    /// <summary>
    /// Finds the first caption track URL in the <c>ytInitialData</c> JSON blob embedded in
    /// the page HTML by scanning for the <c>"captionTracks"</c> key and extracting <c>baseUrl</c>.
    /// </summary>
    private static string? ExtractCaptionTrackUrl(string html)
    {
        const string captionTracksKey = "\"captionTracks\":";
        var tracksIndex = html.IndexOf(captionTracksKey, StringComparison.Ordinal);
        if (tracksIndex < 0)
        {
            return null;
        }

        const string baseUrlKey = "\"baseUrl\":\"";
        var baseUrlIndex = html.IndexOf(baseUrlKey, tracksIndex, StringComparison.Ordinal);
        if (baseUrlIndex < 0)
        {
            return null;
        }

        var start = baseUrlIndex + baseUrlKey.Length;
        var end = html.IndexOf('"', start);
        if (end < 0)
        {
            return null;
        }

        var rawUrl = html[start..end];
        return UnescapeJsonString(rawUrl);
    }

    /// <summary>
    /// Parses the timed-text XML returned by the YouTube caption API into plain text.
    /// Each <c>&lt;text&gt;</c> element maps to one line.
    /// </summary>
    private static string? ParseTranscriptXml(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            return null;
        }

        var doc = XDocument.Parse(xmlContent);
        var lines = doc.Descendants("text")
            .Select(e => System.Net.WebUtility.HtmlDecode(e.Value.Trim()))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        return lines.Length == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Unescapes a JSON string value: converts <c>\\uXXXX</c> sequences and common two-char
    /// escapes (<c>\\n</c>, <c>\\t</c>, <c>\\&amp;</c>, etc.) to their Unicode equivalents.
    /// </summary>
    private static string UnescapeJsonString(string value)
    {
        if (!value.Contains('\\'))
        {
            return value;
        }

        return JsonEscapeRegex().Replace(value, static m =>
        {
            var seq = m.Value;
            if (seq.Length == 6 && seq[1] == 'u')
            {
                return ((char)Convert.ToInt32(seq[2..], 16)).ToString();
            }

            return seq[1] switch
            {
                'n' => "\n",
                'r' => "\r",
                't' => "\t",
                '"' => "\"",
                '\\' => "\\",
                '/' => "/",
                _ => seq[1..] // strip the backslash for unknown escapes
            };
        });
    }

    [GeneratedRegex(@"""shortDescription"":""((?:[^""\\]|\\.)*)""")]
    private static partial Regex ShortDescriptionRegex();

    [GeneratedRegex(@"\\(?:u[0-9a-fA-F]{4}|[nrt""\\/])")]
    private static partial Regex JsonEscapeRegex();
}
