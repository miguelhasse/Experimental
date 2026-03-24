namespace EbookScanner.Core.Models;

/// <summary>
/// Reports progress during a directory scan operation.
/// </summary>
/// <param name="Current">1-based index of the file currently being processed.</param>
/// <param name="Total">Total number of files found to process.</param>
/// <param name="FilePath">Full path of the file currently being processed.</param>
public record ScanProgress(int Current, int Total, string FilePath);
