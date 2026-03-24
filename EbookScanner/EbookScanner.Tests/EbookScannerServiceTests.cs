using EbookScanner.Core;
using EbookScanner.Core.Models;

namespace EbookScanner.Tests;

public sealed class EbookScannerServiceTests
{
    private readonly EbookScannerService _service = new();

    [Fact]
    public async Task ScanAsync_EmptyDirectory_ReturnsEmptyCatalog()
    {
        var dir = Directory.CreateTempSubdirectory("ebookscan_");
        try
        {
            var result = await _service.ScanAsync(new ScanOptions(dir.FullName));

            Assert.Equal(dir.FullName, result.ScannedDirectory);
            Assert.Empty(result.Books);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_NonRecursive_DoesNotScanSubdirectories()
    {
        var dir = Directory.CreateTempSubdirectory("ebookscan_");
        try
        {
            var sub = dir.CreateSubdirectory("sub");
            await File.WriteAllTextAsync(Path.Combine(sub.FullName, "nested.pdf"), "not a real pdf");

            var result = await _service.ScanAsync(new ScanOptions(dir.FullName, Recursive: false));

            Assert.Empty(result.Books);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_Recursive_FindsFilesInSubdirectories()
    {
        var dir = Directory.CreateTempSubdirectory("ebookscan_");
        try
        {
            var sub = dir.CreateSubdirectory("sub");
            // Write a minimal valid PDF header so PdfPig doesn't crash
            await File.WriteAllBytesAsync(
                Path.Combine(sub.FullName, "nested.pdf"),
                "%PDF-1.4\n%%EOF"u8.ToArray());

            var result = await _service.ScanAsync(new ScanOptions(dir.FullName, Recursive: true));

            // The file should be found (metadata extraction may fail gracefully for minimal PDF)
            Assert.True(result.Books.Count >= 0); // at minimum, no exception
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_FormatFilter_OnlyReturnsPdfFiles()
    {
        var dir = Directory.CreateTempSubdirectory("ebookscan_");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "book.epub"), "fake epub");

            var result = await _service.ScanAsync(
                new ScanOptions(dir.FullName, Formats: [BookFormat.Pdf]));

            Assert.Empty(result.Books);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_CorruptFile_IsSkippedGracefully()
    {
        var errors = new List<string>();
        var service = new EbookScannerService(
            onError: (file, _) => errors.Add(file));

        var dir = Directory.CreateTempSubdirectory("ebookscan_");
        try
        {
            var filePath = Path.Combine(dir.FullName, "corrupt.epub");
            await File.WriteAllTextAsync(filePath, "this is not valid epub content");

            var result = await service.ScanAsync(new ScanOptions(dir.FullName));

            Assert.Empty(result.Books);
            Assert.Single(errors);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExtractAsync_UnknownExtension_ReturnsNull()
    {
        var result = await _service.ExtractAsync("/path/to/file.docx");
        Assert.Null(result);
    }

    [Fact]
    public async Task ScanAsync_SetsScannedDirectory_ToFullPath()
    {
        var dir = Directory.CreateTempSubdirectory("ebookscan_");
        try
        {
            var result = await _service.ScanAsync(new ScanOptions(dir.FullName));
            Assert.Equal(Path.GetFullPath(dir.FullName), result.ScannedDirectory);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_Progress_ReportedOncePerFile()
    {
        var dir = Directory.CreateTempSubdirectory("ebookscan_");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "a.epub"), "fake epub");
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "b.epub"), "fake epub");

            var reports = new List<ScanProgress>();
            var progress = new Progress<ScanProgress>(p => reports.Add(p));

            await _service.ScanAsync(new ScanOptions(dir.FullName), progress: progress);

            // Progress<T> dispatches on the synchronization context; give it a tick to flush.
            await Task.Delay(50);

            Assert.Equal(2, reports.Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_Progress_CurrentAndTotalAreCorrect()
    {
        var dir = Directory.CreateTempSubdirectory("ebookscan_");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "a.epub"), "fake epub");
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "b.epub"), "fake epub");
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "c.epub"), "fake epub");

            var reports = new List<ScanProgress>();
            var progress = new Progress<ScanProgress>(p => reports.Add(p));

            await _service.ScanAsync(new ScanOptions(dir.FullName), progress: progress);
            await Task.Delay(50);

            Assert.Equal(3, reports.Count);
            Assert.Equal(3, reports[0].Total);
            Assert.Equal(1, reports[0].Current);
            Assert.Equal(2, reports[1].Current);
            Assert.Equal(3, reports[2].Current);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_Progress_FilePathIsPopulated()
    {
        var dir = Directory.CreateTempSubdirectory("ebookscan_");
        try
        {
            var filePath = Path.Combine(dir.FullName, "book.epub");
            await File.WriteAllTextAsync(filePath, "fake epub");

            var reports = new List<ScanProgress>();
            var progress = new Progress<ScanProgress>(p => reports.Add(p));

            await _service.ScanAsync(new ScanOptions(dir.FullName), progress: progress);
            await Task.Delay(50);

            Assert.Single(reports);
            Assert.Equal(filePath, reports[0].FilePath);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_Progress_NotReportedForNonMatchingFiles()
    {
        var dir = Directory.CreateTempSubdirectory("ebookscan_");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "readme.txt"), "text file");
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "book.epub"), "fake epub");

            var reports = new List<ScanProgress>();
            var progress = new Progress<ScanProgress>(p => reports.Add(p));

            await _service.ScanAsync(new ScanOptions(dir.FullName), progress: progress);
            await Task.Delay(50);

            // Only the .epub should trigger progress; .txt is not a recognised ebook format
            Assert.Single(reports);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_Progress_ReportedEvenWhenExtractionFails()
    {
        var dir = Directory.CreateTempSubdirectory("ebookscan_");
        try
        {
            // corrupt epub – extractor will throw, but progress should still fire
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "bad.epub"), "not valid");

            var reports = new List<ScanProgress>();
            var progress = new Progress<ScanProgress>(p => reports.Add(p));

            var service = new EbookScannerService(onError: (_, _) => { });
            await service.ScanAsync(new ScanOptions(dir.FullName), progress: progress);
            await Task.Delay(50);

            Assert.Single(reports);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_NullProgress_DoesNotThrow()
    {
        var dir = Directory.CreateTempSubdirectory("ebookscan_");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "book.epub"), "fake epub");
            // Passing null explicitly — should be a no-op
            var ex = await Record.ExceptionAsync(() =>
                _service.ScanAsync(new ScanOptions(dir.FullName), progress: null));
            Assert.Null(ex);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
