using BenchmarkDotNet.Attributes;
using MarkItDown.Benchmarks.TestData;
using MarkItDown.Core;
using MarkItDown.Core.Models;

namespace MarkItDown.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("Pdf")]
public class PdfBenchmarks
{
    private MarkItDownService _service = null!;
    private byte[] _data = null!;

    private static readonly StreamInfo Info =
        new(Extension: ".pdf", MimeType: "application/pdf");

    [Params(1, 5, 20)]
    public int Pages { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _service = new MarkItDownService();
        _data = TestDataFactory.CreatePdf(pages: Pages);
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
