using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;
using VersOne.Epub;

namespace MarkItDown.Core.Converters;

public sealed class EpubConverter : DocumentConverter
{
    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        return streamInfo.Extension is ".epub"
            || string.Equals(streamInfo.MimeType, "application/epub+zip", StringComparison.OrdinalIgnoreCase);
    }

    public override async Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        var book = await EpubReader.ReadBookAsync(stream);
        var metadataLines = new List<string>();

        AddMetadataLine(metadataLines, "Title", GetFirstValue(book.Schema?.Package?.Metadata?.Titles, title => title.Title) ?? book.Title);
        AddMetadataLine(metadataLines, "Authors", JoinValues(book.Schema?.Package?.Metadata?.Creators, creator => creator.Creator));
        AddMetadataLine(metadataLines, "Language", GetFirstValue(book.Schema?.Package?.Metadata?.Languages, language => language.Language));
        AddMetadataLine(metadataLines, "Description", GetFirstValue(book.Schema?.Package?.Metadata?.Descriptions, description => description.Description) ?? book.Description);
        AddMetadataLine(metadataLines, "Identifier", GetFirstValue(book.Schema?.Package?.Metadata?.Identifiers, identifier => identifier.Identifier));

        var sections = new List<string>();

        if (metadataLines.Count > 0)
        {
            sections.Add(string.Join(Environment.NewLine, metadataLines));
        }

        var navTitleMap = book.Navigation is { } nav ? BuildNavTitleMap(nav) : [];

        foreach (var chapter in book.ReadingOrder)
        {
            if (string.IsNullOrWhiteSpace(chapter.Content))
            {
                continue;
            }

            var markdown = HtmlMarkdownConverter.Convert(chapter.Content).Markdown;

            if (!navTitleMap.TryGetValue(chapter.FilePath, out var navTitle))
            {
                navTitleMap.TryGetValue(chapter.Key, out navTitle);
            }

            if (string.IsNullOrWhiteSpace(navTitle))
            {
                if (!string.IsNullOrWhiteSpace(markdown))
                {
                    sections.Add(markdown);
                }
            }
            else
            {
                sections.Add($"## {navTitle}{Environment.NewLine}{Environment.NewLine}{markdown}");
            }
        }

        return new DocumentConverterResult(string.Join(Environment.NewLine + Environment.NewLine, sections), book.Title);
    }

    private static Dictionary<string, string> BuildNavTitleMap(List<EpubNavigationItem> navItems)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        BuildNavTitleMapCore(navItems, map);
        return map;
    }

    private static void BuildNavTitleMapCore(List<EpubNavigationItem> items, Dictionary<string, string> map)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Title) && item.Link?.ContentFilePath is { Length: > 0 } path)
            {
                map.TryAdd(path, item.Title);
            }

            if (item.NestedItems is { Count: > 0 })
            {
                BuildNavTitleMapCore(item.NestedItems, map);
            }
        }
    }

    private static void AddMetadataLine(List<string> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"**{label}:** {value}");
        }
    }

    private static string? JoinValues<T>(IEnumerable<T>? items, Func<T, string?> selector)
    {
        if (items is null)
        {
            return null;
        }

        var values = items
            .Select(selector)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return values.Length > 0 ? string.Join(", ", values) : null;
    }

    private static string? GetFirstValue<T>(IEnumerable<T>? items, Func<T, string?> selector)
    {
        if (items is null)
        {
            return null;
        }

        return items
            .Select(selector)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
