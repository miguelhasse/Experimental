using System.Text;
using EbookScanner.Core.Models;

namespace EbookScanner.Core.Formatters;

public sealed class MarkdownFormatter : IMetadataFormatter
{
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

        foreach (var book in books)
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            var title = book.Title ?? Path.GetFileNameWithoutExtension(book.FileName);
            sb.AppendLine($"## {title}");
            sb.AppendLine();
            sb.AppendLine("| Field | Value |");
            sb.AppendLine("|-------|-------|");
            sb.AppendLine($"| Format | {book.Format} |");

            if (book.Authors is { Length: > 0 })
                sb.AppendLine($"| Authors | {string.Join(", ", book.Authors)} |");

            if (!string.IsNullOrWhiteSpace(book.Publisher))
                sb.AppendLine($"| Publisher | {book.Publisher} |");

            if (!string.IsNullOrWhiteSpace(book.Language))
                sb.AppendLine($"| Language | {book.Language} |");

            if (!string.IsNullOrWhiteSpace(book.Isbn))
                sb.AppendLine($"| ISBN | {book.Isbn} |");

            if (book.PageCount.HasValue)
                sb.AppendLine($"| Pages | {book.PageCount} |");

            if (book.PublishedDate.HasValue)
                sb.AppendLine($"| Published | {book.PublishedDate:yyyy-MM-dd} |");

            if (book.ModifiedDate.HasValue)
                sb.AppendLine($"| Modified | {book.ModifiedDate:yyyy-MM-dd} |");

            if (!string.IsNullOrWhiteSpace(book.Description))
            {
                var desc = book.Description.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
                if (desc.Length > 200) desc = desc[..200] + "…";
                sb.AppendLine($"| Description | {desc} |");
            }

            if (book.Tags is { Length: > 0 })
                sb.AppendLine($"| Tags | {string.Join(", ", book.Tags)} |");

            sb.AppendLine($"| File | {book.FileName} |");
            sb.AppendLine($"| Size | {FormatFileSize(book.FileSizeBytes)} |");
        }

        return sb.ToString();
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1_024) return $"{bytes / 1_024.0:F1} KB";
        return $"{bytes} B";
    }
}
