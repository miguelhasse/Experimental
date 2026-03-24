using EbookScanner.Core.Extractors;
using EbookScanner.Core.Models;

namespace EbookScanner.Core;

public sealed class EbookScannerService(Action<string, Exception>? onError = null)
{
    private static readonly Dictionary<BookFormat, string[]> FormatExtensions = new()
    {
        [BookFormat.Pdf]  = [".pdf"],
        [BookFormat.Epub] = [".epub"],
        [BookFormat.Mobi] = [".mobi", ".azw", ".azw3", ".prc"],
        [BookFormat.Chm]  = [".chm"],
    };

    private readonly List<BookMetadataExtractor> _extractors =
    [
        new PdfMetadataExtractor(),
        new EpubMetadataExtractor(),
        new MobiMetadataExtractor(),
        new ChmMetadataExtractor(),
    ];

    /// <summary>
    /// Scans the specified directory for supported book files and extracts their metadata asynchronously.
    /// </summary>
    /// <remarks>If no formats are specified in the options, the scan includes PDF, EPUB, and MOBI files by
    /// default. The method invokes an error handler for files that cannot be processed, and continues scanning
    /// remaining files. The scan can be cancelled via the provided cancellation token.</remarks>
    /// <param name="options">The options that control the scan operation, including the target directory, file formats to include, and
    /// whether to search subdirectories.</param>
    /// <param name="progress">An optional progress reporter that receives a <see cref="ScanProgress"/> update before each file is processed.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the scan operation.</param>
    /// <returns>A task that represents the asynchronous scan operation. The task result contains a ScanResult with the scanned
    /// directory path, scan timestamp, and a collection of extracted book metadata.</returns>
    public async Task<ScanResult> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var formats = options.Formats ?? [BookFormat.Pdf, BookFormat.Epub, BookFormat.Mobi, BookFormat.Chm];
        var extensions = formats
            .SelectMany(f => FormatExtensions[f])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var searchOption = options.Recursive
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        var files = Directory
            .EnumerateFiles(options.Directory, "*", searchOption)
            .Where(f => extensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var books = new List<BookMetadata>(files.Count);

        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = files[i];
            progress?.Report(new ScanProgress(Current: i + 1, Total: files.Count, FilePath: file));

            var extractor = _extractors.FirstOrDefault(e => e.Accepts(file));
            if (extractor is null) continue;

            try
            {
                var metadata = await extractor.ExtractAsync(file, cancellationToken);
                books.Add(metadata);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                onError?.Invoke(file, ex);
            }
        }

        return new ScanResult(
            ScannedDirectory: Path.GetFullPath(options.Directory),
            ScannedAt: DateTimeOffset.UtcNow,
            Books: books);
    }

    /// <summary>
    /// Asynchronously extracts metadata from the specified book file, if a suitable extractor is available.
    /// </summary>
    /// <param name="filePath">The full path to the book file from which to extract metadata. Cannot be null or empty.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the extraction operation.</param>
    /// <returns>A task that represents the asynchronous extraction operation. The task result contains a <see
    /// cref="BookMetadata"/> object with the extracted metadata if extraction is successful; otherwise, <see
    /// langword="null"/> if no suitable extractor is found.</returns>
    public async Task<BookMetadata?> ExtractAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var extractor = _extractors.FirstOrDefault(e => e.Accepts(filePath));
        return (extractor is null) ? null : await extractor.ExtractAsync(filePath, cancellationToken);
    }
}
