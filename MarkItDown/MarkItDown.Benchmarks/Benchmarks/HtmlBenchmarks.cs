using BenchmarkDotNet.Attributes;
using MarkItDown.Benchmarks.TestData;
using MarkItDown.Core;
using MarkItDown.Core.Models;

namespace MarkItDown.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("Html")]
public class HtmlBenchmarks
{
    private MarkItDownService _service = null!;
    private byte[] _smallBytes = null!;
    private byte[] _largeBytes = null!;

    private static readonly StreamInfo Info = new(Extension: ".html", MimeType: "text/html");

    [GlobalSetup]
    public void Setup()
    {
        _service = new MarkItDownService();
        _smallBytes = TestDataFactory.CreateHtml(sections: 3);
        _largeBytes = TestDataFactory.CreateHtml(sections: 50);
    }

    [GlobalCleanup]
    public void Cleanup() => _service.Dispose();

    [Benchmark(Baseline = true, Description = "small (~1 KB)")]
    public async Task<DocumentConverterResult> Small()
    {
        await using var stream = new MemoryStream(_smallBytes);
        return await _service.ConvertStreamAsync(stream, Info);
    }

    [Benchmark(Description = "large (~20 KB)")]
    public async Task<DocumentConverterResult> Large()
    {
        await using var stream = new MemoryStream(_largeBytes);
        return await _service.ConvertStreamAsync(stream, Info);
    }
}
