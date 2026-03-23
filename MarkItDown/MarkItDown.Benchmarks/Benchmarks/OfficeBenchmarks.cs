using BenchmarkDotNet.Attributes;
using MarkItDown.Benchmarks.TestData;
using MarkItDown.Core;
using MarkItDown.Core.Models;

namespace MarkItDown.Benchmarks.Benchmarks;

// ---------------------------------------------------------------------------
// DOCX
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
[BenchmarkCategory("Office", "Docx")]
public class DocxBenchmarks
{
    private MarkItDownService _service = null!;
    private byte[] _smallBytes = null!;
    private byte[] _largeBytes = null!;

    private static readonly StreamInfo Info =
        new(Extension: ".docx",
            MimeType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

    [GlobalSetup]
    public void Setup()
    {
        _service = new MarkItDownService();
        _smallBytes = TestDataFactory.CreateDocx(paragraphs: 5);
        _largeBytes = TestDataFactory.CreateDocx(paragraphs: 100);
    }

    [GlobalCleanup]
    public void Cleanup() => _service.Dispose();

    [Benchmark(Baseline = true, Description = "small (5 paragraphs)")]
    public async Task<DocumentConverterResult> Small()
    {
        await using var stream = new MemoryStream(_smallBytes);
        return await _service.ConvertStreamAsync(stream, Info);
    }

    [Benchmark(Description = "large (100 paragraphs)")]
    public async Task<DocumentConverterResult> Large()
    {
        await using var stream = new MemoryStream(_largeBytes);
        return await _service.ConvertStreamAsync(stream, Info);
    }
}

// ---------------------------------------------------------------------------
// XLSX
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
[BenchmarkCategory("Office", "Xlsx")]
public class XlsxBenchmarks
{
    private MarkItDownService _service = null!;
    private byte[] _data = null!;

    private static readonly StreamInfo Info =
        new(Extension: ".xlsx",
            MimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

    [Params(10, 200)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _service = new MarkItDownService();
        _data = TestDataFactory.CreateXlsx(rows: Rows, cols: 6);
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

// ---------------------------------------------------------------------------
// PPTX
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
[BenchmarkCategory("Office", "Pptx")]
public class PptxBenchmarks
{
    private MarkItDownService _service = null!;
    private byte[] _fewSlides = null!;
    private byte[] _manySlides = null!;

    private static readonly StreamInfo Info =
        new(Extension: ".pptx",
            MimeType: "application/vnd.openxmlformats-officedocument.presentationml.presentation");

    [GlobalSetup]
    public void Setup()
    {
        _service = new MarkItDownService();
        _fewSlides = TestDataFactory.CreatePptx(slides: 3);
        _manySlides = TestDataFactory.CreatePptx(slides: 20);
    }

    [GlobalCleanup]
    public void Cleanup() => _service.Dispose();

    [Benchmark(Baseline = true, Description = "3 slides")]
    public async Task<DocumentConverterResult> FewSlides()
    {
        await using var stream = new MemoryStream(_fewSlides);
        return await _service.ConvertStreamAsync(stream, Info);
    }

    [Benchmark(Description = "20 slides")]
    public async Task<DocumentConverterResult> ManySlides()
    {
        await using var stream = new MemoryStream(_manySlides);
        return await _service.ConvertStreamAsync(stream, Info);
    }
}
