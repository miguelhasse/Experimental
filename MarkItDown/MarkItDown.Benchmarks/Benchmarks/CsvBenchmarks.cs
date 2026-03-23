using BenchmarkDotNet.Attributes;
using MarkItDown.Benchmarks.TestData;
using MarkItDown.Core;
using MarkItDown.Core.Models;

namespace MarkItDown.Benchmarks.Benchmarks;

/// <summary>
/// Benchmarks CsvConverter across a range of row counts.
/// [Params] causes BenchmarkDotNet to run one benchmark per value,
/// providing a clean scaling profile.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Csv")]
public class CsvBenchmarks
{
    private MarkItDownService _service = null!;
    private byte[] _data = null!;

    private static readonly StreamInfo Info = new(Extension: ".csv", MimeType: "text/csv");

    [Params(10, 100, 1_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _service = new MarkItDownService();
        _data = TestDataFactory.CreateCsv(rows: Rows, cols: 6);
    }

    [GlobalCleanup]
    public void Cleanup() => _service.Dispose();

    [Benchmark]
    public async Task<DocumentConverterResult> Convert()
    {
        await using var stream = new MemoryStream(_data);
        return await _service.ConvertStreamAsync(stream, Info);
    }
}
