using BenchmarkDotNet.Attributes;
using MarkItDown.Benchmarks.TestData;
using MarkItDown.Core;
using MarkItDown.Core.Models;

namespace MarkItDown.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("Epub")]
public class EpubBenchmarks
{
    private MarkItDownService _service = null!;
    private byte[] _smallEpub = null!;
    private byte[] _largeEpub = null!;

    private static readonly StreamInfo Info =
        new(Extension: ".epub", MimeType: "application/epub+zip");

    [GlobalSetup]
    public void Setup()
    {
        _service = new MarkItDownService();
        _smallEpub = TestDataFactory.CreateEpub(chapters: 2);
        _largeEpub = TestDataFactory.CreateEpub(chapters: 15);
    }

    [GlobalCleanup]
    public void Cleanup() => _service.Dispose();

    [Benchmark(Baseline = true, Description = "2 chapters")]
    public async Task<DocumentConverterResult> Small()
    {
        await using var stream = new MemoryStream(_smallEpub);
        return await _service.ConvertStreamAsync(stream, Info);
    }

    [Benchmark(Description = "15 chapters")]
    public async Task<DocumentConverterResult> Large()
    {
        await using var stream = new MemoryStream(_largeEpub);
        return await _service.ConvertStreamAsync(stream, Info);
    }
}
