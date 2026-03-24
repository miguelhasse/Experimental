using EbookScanner.Core.Models;
using UglyToad.PdfPig;

namespace EbookScanner.Core.Extractors;

public sealed class PdfMetadataExtractor : BookMetadataExtractor
{
    public override bool Accepts(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase);

    public override Task<BookMetadata> ExtractAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(filePath);
        using var document = PdfDocument.Open(filePath);
        var info = document.Information;

        var authors = SplitDelimited(info.Author);
        var tags = SplitDelimited(info.Keywords);

        return Task.FromResult(new BookMetadata(
            FilePath: filePath,
            FileName: fileInfo.Name,
            Format: "PDF",
            FileSizeBytes: fileInfo.Length,
            Title: NullIfEmpty(info.Title),
            Authors: authors,
            Publisher: NullIfEmpty(info.Creator),
            Description: NullIfEmpty(info.Subject),
            Language: null,
            Isbn: null,
            PublishedDate: ParsePdfDate(info.CreationDate),
            ModifiedDate: ParsePdfDate(info.ModifiedDate),
            PageCount: document.NumberOfPages,
            Tags: tags));
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string[]? SplitDelimited(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var parts = value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 ? parts : null;
    }

    // PDF date format: D:YYYYMMDDHHmmSSOHH'mm' where O is +, -, or Z
    private static DateTimeOffset? ParsePdfDate(string? pdfDate)
    {
        if (string.IsNullOrWhiteSpace(pdfDate))
            return null;

        var s = pdfDate.Trim();
        if (s.StartsWith("D:", StringComparison.OrdinalIgnoreCase))
            s = s[2..];

        // Minimum: YYYYMMDD
        if (s.Length < 8)
            return null;

        try
        {
            var year = int.Parse(s[..4]);
            var month = int.Parse(s[4..6]);
            var day = int.Parse(s[6..8]);
            var hour = s.Length >= 10 ? int.Parse(s[8..10]) : 0;
            var minute = s.Length >= 12 ? int.Parse(s[10..12]) : 0;
            var second = s.Length >= 14 ? int.Parse(s[12..14]) : 0;

            var offset = TimeSpan.Zero;
            if (s.Length >= 15)
            {
                var sign = s[14] == '-' ? -1 : 1;
                if ((s[14] == '+' || s[14] == '-') && s.Length >= 17)
                {
                    var oh = int.Parse(s[15..17]);
                    var om = s.Length >= 20 ? int.Parse(s[18..20]) : 0;
                    offset = new TimeSpan(sign * oh, sign * om, 0);
                }
            }

            return new DateTimeOffset(year, month, day, hour, minute, second, offset);
        }
        catch
        {
            return null;
        }
    }
}
