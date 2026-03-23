using BenchmarkDotNet.Attributes;
using MarkItDown.Benchmarks.TestData;
using MarkItDown.Core;
using MarkItDown.Core.Models;

namespace MarkItDown.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("Rss")]
public class RssBenchmarks
{
    private MarkItDownService _service = null!;
    private byte[] _smallFeed = null!;
    private byte[] _largeFeed = null!;

    private static readonly StreamInfo Info =
        new(Extension: ".rss", MimeType: "application/rss+xml");

    [GlobalSetup]
    public void Setup()
    {
        _service = new MarkItDownService();
        _smallFeed = TestDataFactory.CreateRss(items: 5);
        _largeFeed = TestDataFactory.CreateRss(items: 100);
    }

    [GlobalCleanup]
    public void Cleanup() => _service.Dispose();

    [Benchmark(Baseline = true, Description = "5 items")]
    public async Task<DocumentConverterResult> Small()
    {
        await using var stream = new MemoryStream(_smallFeed);
        return await _service.ConvertStreamAsync(stream, Info);
    }

    [Benchmark(Description = "100 items")]
    public async Task<DocumentConverterResult> Large()
    {
        await using var stream = new MemoryStream(_largeFeed);
        return await _service.ConvertStreamAsync(stream, Info);
    }
}
