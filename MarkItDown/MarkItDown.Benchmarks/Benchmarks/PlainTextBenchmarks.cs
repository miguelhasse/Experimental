using BenchmarkDotNet.Attributes;
using MarkItDown.Benchmarks.TestData;
using MarkItDown.Core;
using MarkItDown.Core.Models;

namespace MarkItDown.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("PlainText")]
public class PlainTextBenchmarks
{
    private MarkItDownService _service = null!;
    private byte[] _smallBytes = null!;
    private byte[] _mediumBytes = null!;
    private byte[] _largeBytes = null!;

    private static readonly StreamInfo Info = new(Extension: ".txt", MimeType: "text/plain");

    [GlobalSetup]
    public void Setup()
    {
        _service = new MarkItDownService();
        _smallBytes = TestDataFactory.CreatePlainText(paragraphs: 2);
        _mediumBytes = TestDataFactory.CreatePlainText(paragraphs: 50);
        _largeBytes = TestDataFactory.CreatePlainText(paragraphs: 500);
    }

    [GlobalCleanup]
    public void Cleanup() => _service.Dispose();

    [Benchmark(Baseline = true, Description = "small (~200 B)")]
    public async Task<DocumentConverterResult> Small()
    {
        await using var stream = new MemoryStream(_smallBytes);
        return await _service.ConvertStreamAsync(stream, Info);
    }

    [Benchmark(Description = "medium (~5 KB)")]
    public async Task<DocumentConverterResult> Medium()
    {
        await using var stream = new MemoryStream(_mediumBytes);
        return await _service.ConvertStreamAsync(stream, Info);
    }

    [Benchmark(Description = "large (~50 KB)")]
    public async Task<DocumentConverterResult> Large()
    {
        await using var stream = new MemoryStream(_largeBytes);
        return await _service.ConvertStreamAsync(stream, Info);
    }
}
