namespace EbookScanner.Core.Models;

public enum BookFormat
{
    Pdf,
    Epub,
    Mobi,
    Chm,
}

public record ScanOptions(
    string Directory,
    bool Recursive = false,
    BookFormat[]? Formats = null);
