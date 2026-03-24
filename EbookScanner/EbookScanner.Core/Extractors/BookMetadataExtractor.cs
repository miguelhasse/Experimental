using EbookScanner.Core.Models;

namespace EbookScanner.Core.Extractors;

public abstract class BookMetadataExtractor
{
    public virtual string Name => GetType().Name;

    public abstract bool Accepts(string filePath);

    public abstract Task<BookMetadata> ExtractAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}
