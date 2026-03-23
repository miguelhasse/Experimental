using BenchmarkDotNet.Attributes;
using MarkItDown.Benchmarks.TestData;
using MarkItDown.Core;
using MarkItDown.Core.Models;

namespace MarkItDown.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("Image")]
public class ImageBenchmarks
{
    private MarkItDownService _service = null!;
    private byte[] _bmpBytes = null!;

    private static readonly StreamInfo Info =
        new(FileName: "benchmark.bmp", Extension: ".bmp", MimeType: "image/bmp");

    [GlobalSetup]
    public void Setup()
    {
        _service = new MarkItDownService();
        _bmpBytes = TestDataFactory.CreateBmp();
    }

    [GlobalCleanup]
    public void Cleanup() => _service.Dispose();

    /// <summary>
    /// Measures MetadataExtractor overhead: opening the stream, reading BMP
    /// header fields, and formatting the result as Markdown lists.
    /// </summary>
    [Benchmark]
    public async Task<DocumentConverterResult> ReadMetadata()
    {
        await using var stream = new MemoryStream(_bmpBytes);
        return await _service.ConvertStreamAsync(stream, Info);
    }
}
