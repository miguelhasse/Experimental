using EbookScanner.Core.Models;
using VersOne.Epub;

namespace EbookScanner.Core.Extractors;

public sealed class EpubMetadataExtractor : BookMetadataExtractor
{
    public override bool Accepts(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".epub", StringComparison.OrdinalIgnoreCase);

    public override async Task<BookMetadata> ExtractAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(filePath);
        var book = await EpubReader.ReadBookAsync(filePath);
        var metadata = book.Schema.Package.Metadata;

        var authors = book.AuthorList.Count > 0 ? book.AuthorList.ToArray() : null;
        var tags = metadata.Subjects.Select(s => s.Subject).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        var language = metadata.Languages.FirstOrDefault()?.Language;
        var isbn = metadata.Identifiers
            .FirstOrDefault(id => id.Scheme != null &&
                id.Scheme.Contains("isbn", StringComparison.OrdinalIgnoreCase))
            ?.Identifier;
        var publishedDate = ParseEpubDate(metadata.Dates
            .FirstOrDefault(d => d.Event == null ||
                d.Event.Contains("publication", StringComparison.OrdinalIgnoreCase) ||
                d.Event.Contains("issued", StringComparison.OrdinalIgnoreCase))
            ?.Date ?? metadata.Dates.FirstOrDefault()?.Date);
        var publisher = metadata.Publishers.FirstOrDefault()?.Publisher;
        var description = metadata.Descriptions.FirstOrDefault()?.Description;

        return new BookMetadata(
            FilePath: filePath,
            FileName: fileInfo.Name,
            Format: "EPUB",
            FileSizeBytes: fileInfo.Length,
            Title: string.IsNullOrWhiteSpace(book.Title) ? null : book.Title,
            Authors: authors,
            Publisher: string.IsNullOrWhiteSpace(publisher) ? null : publisher,
            Description: string.IsNullOrWhiteSpace(description) ? null : description,
            Language: string.IsNullOrWhiteSpace(language) ? null : language,
            Isbn: string.IsNullOrWhiteSpace(isbn) ? null : isbn,
            PublishedDate: publishedDate,
            ModifiedDate: null,
            PageCount: null,
            Tags: tags.Length > 0 ? tags : null);
    }

    private static DateTimeOffset? ParseEpubDate(string? dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr))
            return null;

        if (DateTimeOffset.TryParse(dateStr, out var result))
            return result;

        // EPUB dates can be just a year: "2021"
        if (dateStr.Length == 4 && int.TryParse(dateStr, out var year))
            return new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);

        return null;
    }
}
