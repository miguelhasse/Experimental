using System.Text.RegularExpressions;
using HtmlAgilityPack;
using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;
using ReverseMarkdown;

namespace MarkItDown.Core.Converters;

public sealed partial class BingSerpConverter : DocumentConverter
{
    [GeneratedRegex(@"^https://www\.bing\.com/search\?q=")]
    private static partial Regex BingSearchUrlRegex();

    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        if (streamInfo.Url is null || !BingSearchUrlRegex().IsMatch(streamInfo.Url))
            return false;

        var mime = streamInfo.MimeType?.ToLowerInvariant() ?? string.Empty;
        var ext = streamInfo.Extension?.ToLowerInvariant() ?? string.Empty;

        return ext is ".html" or ".htm"
            || mime.StartsWith("text/html", StringComparison.Ordinal)
            || mime.StartsWith("application/xhtml", StringComparison.Ordinal);
    }

    public override async Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        var html = await StreamHelpers.ReadAllTextAsync(stream, streamInfo, cancellationToken);

        var query = ExtractQueryParam(streamInfo.Url ?? string.Empty, "q");

        var document = new HtmlDocument();
        document.LoadHtml(html);

        // Append a space after tptt spans so titles don't run into descriptions
        var tpttNodes = document.DocumentNode.SelectNodes("//*[contains(@class,'tptt')]");
        if (tpttNodes is not null)
        {
            foreach (var tptt in tpttNodes)
                tptt.InnerHtml = tptt.InnerHtml + " ";
        }

        // Remove icon elements
        var slugNodes = document.DocumentNode.SelectNodes("//*[contains(@class,'algoSlug_icon')]");
        if (slugNodes is not null)
        {
            foreach (var slug in slugNodes.ToArray())
                slug.Remove();
        }

        var converter = new Converter(new Config
        {
            UnknownTags = Config.UnknownTagsOption.Bypass
        });

        var results = new List<string>();
        var algoNodes = document.DocumentNode.SelectNodes("//*[contains(@class,'b_algo')]");
        if (algoNodes is not null)
        {
            foreach (var result in algoNodes)
            {
                // Rewrite Bing redirect URLs to direct URLs
                var anchors = result.SelectNodes(".//a[@href]");
                if (anchors is not null)
                {
                    foreach (var anchor in anchors)
                    {
                        var href = anchor.GetAttributeValue("href", string.Empty);
                        var uParam = ExtractQueryParam(href, "u");
                        if (uParam is not null)
                        {
                            var decoded = TryDecodeBase64Url(uParam[2..] + "==");
                            if (decoded is not null)
                                anchor.SetAttributeValue("href", decoded);
                        }
                    }
                }

                var mdResult = converter.Convert(result.OuterHtml).Trim();
                var lines = mdResult
                    .Split('\n', StringSplitOptions.None)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0);
                results.Add(string.Join("\n", lines));
            }
        }

        var title = document.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim();
        if (!string.IsNullOrWhiteSpace(title))
            title = HtmlEntity.DeEntitize(title);

        var markdown = $"## A Bing search for '{query}' found the following results:\n\n"
                      + string.Join("\n\n", results);

        return new DocumentConverterResult(markdown, string.IsNullOrWhiteSpace(title) ? null : title);
    }

    private static string? ExtractQueryParam(string url, string paramName)
    {
        var queryStart = url.IndexOf('?');
        if (queryStart < 0) return null;

        var query = url[(queryStart + 1)..];
        foreach (var part in query.Split('&'))
        {
            var eqIdx = part.IndexOf('=');
            if (eqIdx < 0) continue;

            var key = Uri.UnescapeDataString(part[..eqIdx]);
            if (string.Equals(key, paramName, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(part[(eqIdx + 1)..]);
        }

        return null;
    }

    private static string? TryDecodeBase64Url(string value)
    {
        // Base64URL uses '-' and '_' instead of '+' and '/'
        var base64 = value.Replace('-', '+').Replace('_', '/');
        // Pad to multiple of 4
        var padding = (4 - base64.Length % 4) % 4;
        base64 += new string('=', padding);

        try
        {
            var bytes = Convert.FromBase64String(base64);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}
