using BenchmarkDotNet.Attributes;
using MarkItDown.Benchmarks.TestData;
using MarkItDown.Core;
using MarkItDown.Core.Models;

namespace MarkItDown.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("Mobi")]
public class MobiBenchmarks
{
    private MarkItDownService _service = null!;
    private byte[] _smallMobi = null!;
    private byte[] _largeMobi = null!;

    private static readonly StreamInfo Info =
        new(Extension: ".mobi", MimeType: "application/x-mobipocket-ebook");

    [GlobalSetup]
    public void Setup()
    {
        _service = new MarkItDownService();
        _smallMobi = TestDataFactory.CreateMobi(sections: 2);
        _largeMobi = TestDataFactory.CreateMobi(sections: 10);
    }

    [GlobalCleanup]
    public void Cleanup() => _service.Dispose();

    [Benchmark(Baseline = true, Description = "2 sections")]
    public async Task<DocumentConverterResult> Small()
    {
        await using var stream = new MemoryStream(_smallMobi);
        return await _service.ConvertStreamAsync(stream, Info);
    }

    [Benchmark(Description = "10 sections")]
    public async Task<DocumentConverterResult> Large()
    {
        await using var stream = new MemoryStream(_largeMobi);
        return await _service.ConvertStreamAsync(stream, Info);
    }
}
