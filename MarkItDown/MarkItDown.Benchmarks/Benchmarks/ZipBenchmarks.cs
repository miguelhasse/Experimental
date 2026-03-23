using BenchmarkDotNet.Attributes;
using MarkItDown.Benchmarks.TestData;
using MarkItDown.Core;
using MarkItDown.Core.Models;

namespace MarkItDown.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("Zip")]
public class ZipBenchmarks
{
    private MarkItDownService _service = null!;
    private byte[] _fewEntries = null!;
    private byte[] _manyEntries = null!;

    private static readonly StreamInfo Info =
        new(Extension: ".zip", MimeType: "application/zip");

    [GlobalSetup]
    public void Setup()
    {
        _service = new MarkItDownService();
        _fewEntries = TestDataFactory.CreateZip(entries: 3);
        _manyEntries = TestDataFactory.CreateZip(entries: 20);
    }

    [GlobalCleanup]
    public void Cleanup() => _service.Dispose();

    [Benchmark(Baseline = true, Description = "3 text entries")]
    public async Task<DocumentConverterResult> FewEntries()
    {
        await using var stream = new MemoryStream(_fewEntries);
        return await _service.ConvertStreamAsync(stream, Info);
    }

    [Benchmark(Description = "20 text entries")]
    public async Task<DocumentConverterResult> ManyEntries()
    {
        await using var stream = new MemoryStream(_manyEntries);
        return await _service.ConvertStreamAsync(stream, Info);
    }
}
