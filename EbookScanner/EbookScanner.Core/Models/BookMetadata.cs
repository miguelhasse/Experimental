namespace EbookScanner.Core.Models;

public record BookMetadata(
    string FilePath,
    string FileName,
    string Format,
    long FileSizeBytes,
    string? Title = null,
    string[]? Authors = null,
    string? Publisher = null,
    string? Description = null,
    string? Language = null,
    string? Isbn = null,
    DateTimeOffset? PublishedDate = null,
    DateTimeOffset? ModifiedDate = null,
    int? PageCount = null,
    string[]? Tags = null);
