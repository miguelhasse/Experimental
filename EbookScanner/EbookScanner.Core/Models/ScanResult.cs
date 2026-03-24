namespace EbookScanner.Core.Models;

public record ScanResult(
    string ScannedDirectory,
    DateTimeOffset ScannedAt,
    IReadOnlyList<BookMetadata> Books);
