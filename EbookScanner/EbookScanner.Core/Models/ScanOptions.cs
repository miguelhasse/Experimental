namespace EbookScanner.Core.Models;

public enum BookFormat
{
    Pdf,
    Epub,
    Mobi,
}

public record ScanOptions(
    string Directory,
    bool Recursive = false,
    BookFormat[]? Formats = null);
