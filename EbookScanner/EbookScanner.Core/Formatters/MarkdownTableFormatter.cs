using System.Text;
using EbookScanner.Core.Models;

namespace EbookScanner.Core.Formatters;

public sealed class MarkdownTableFormatter : IMetadataFormatter
{
    public static readonly IReadOnlyList<string> ValidColumns =
    [
        "name", "location", "format", "authors", "publisher",
        "language", "isbn", "published", "modified", "pages",
        "tags", "size", "description",
    ];

    public static readonly IReadOnlyList<string> DefaultColumns =
        ["name", "location", "format", "authors", "published", "size"];

    private readonly IReadOnlyList<string> _columns;

    public MarkdownTableFormatter(IReadOnlyList<string>? columns = null)
    {
        _columns = columns is { Count: > 0 } ? columns : DefaultColumns;
    }

    public string Format(ScanResult result)
    {
        var sb = new StringBuilder();
        var books = result.Books;

        var pdfCount = books.Count(b => b.Format == "PDF");
        var epubCount = books.Count(b => b.Format == "EPUB");
        var mobiCount = books.Count(b => b.Format == "MOBI");

        sb.AppendLine("# Ebook Catalog");
        sb.AppendLine();
        sb.AppendLine($"**Directory:** {result.ScannedDirectory}  ");
        sb.AppendLine($"**Scanned:** {result.ScannedAt:yyyy-MM-dd HH:mm:ss}  ");
        sb.Append($"**Total:** {books.Count} book{(books.Count != 1 ? "s" : "")}");
        if (pdfCount > 0 || epubCount > 0 || mobiCount > 0)
        {
            var parts = new List<string>();
            if (pdfCount > 0) parts.Add($"PDF: {pdfCount}");
            if (epubCount > 0) parts.Add($"EPUB: {epubCount}");
            if (mobiCount > 0) parts.Add($"MOBI: {mobiCount}");
            sb.Append($" · {string.Join(" · ", parts)}");
        }
        sb.AppendLine();
        sb.AppendLine();

        // Header row
        sb.Append('|');
        foreach (var col in _columns)
            sb.Append($" {ColumnHeader(col)} |");
        sb.AppendLine();

        // Separator row
        sb.Append('|');
        foreach (var _ in _columns)
            sb.Append("---|");
        sb.AppendLine();

        // Data rows
        foreach (var book in books)
        {
            sb.Append('|');
            foreach (var col in _columns)
                sb.Append($" {EscapeCell(GetColumnValue(book, col))} |");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string ColumnHeader(string column) => column switch
    {
        "name"        => "Name",
        "location"    => "Location",
        "format"      => "Format",
        "authors"     => "Authors",
        "publisher"   => "Publisher",
        "language"    => "Language",
        "isbn"        => "ISBN",
        "published"   => "Published",
        "modified"    => "Modified",
        "pages"       => "Pages",
        "tags"        => "Tags",
        "size"        => "Size",
        "description" => "Description",
        _             => column,
    };

    private static string GetColumnValue(BookMetadata book, string column) => column switch
    {
        "name"        => book.Title ?? Path.GetFileNameWithoutExtension(book.FileName),
        "location"    => book.FilePath,
        "format"      => book.Format,
        "authors"     => book.Authors is { Length: > 0 } ? string.Join(", ", book.Authors) : "",
        "publisher"   => book.Publisher ?? "",
        "language"    => book.Language ?? "",
        "isbn"        => book.Isbn ?? "",
        "published"   => book.PublishedDate.HasValue ? $"{book.PublishedDate:yyyy-MM-dd}" : "",
        "modified"    => book.ModifiedDate.HasValue ? $"{book.ModifiedDate:yyyy-MM-dd}" : "",
        "pages"       => book.PageCount.HasValue ? book.PageCount.Value.ToString() : "",
        "tags"        => book.Tags is { Length: > 0 } ? string.Join(", ", book.Tags) : "",
        "size"        => FormatFileSize(book.FileSizeBytes),
        "description" => TruncateDescription(book.Description),
        _             => "",
    };

    private static string TruncateDescription(string? description)
    {
        if (string.IsNullOrEmpty(description)) return "";
        var flat = description.Replace("\r", "").Replace("\n", " ");
        return flat.Length > 100 ? flat[..100] + "…" : flat;
    }

    private static string EscapeCell(string value) =>
        value.Replace("|", "\\|");

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1_024) return $"{bytes / 1_024.0:F1} KB";
        return $"{bytes} B";
    }
}
