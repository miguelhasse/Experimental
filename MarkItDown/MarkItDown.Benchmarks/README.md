# MarkItDown.Benchmarks

Performance benchmarks for `MarkItDown.Core` using [BenchmarkDotNet](https://benchmarkdotnet.org/).

**Target:** `net10.0` · **BenchmarkDotNet:** 0.15.8

---

## Running benchmarks

Benchmarks **must** be run in `Release` configuration — `Debug` builds disable JIT optimizations and produce meaningless results.

```powershell
# Interactive menu — choose which benchmark class(es) to run
dotnet run -c Release --project MarkItDown.Benchmarks

# Run everything
dotnet run -c Release --project MarkItDown.Benchmarks -- --all

# Filter by class or method name (glob patterns supported)
dotnet run -c Release --project MarkItDown.Benchmarks -- --filter *Service*
dotnet run -c Release --project MarkItDown.Benchmarks -- --filter *Csv*
dotnet run -c Release --project MarkItDown.Benchmarks -- --filter *Office*

# Quick validation run (short job — fewer iterations, faster, less precise)
dotnet run -c Release --project MarkItDown.Benchmarks -- --job short --filter *
```

Results are written to `BenchmarkDotNet.Artifacts/` in the project directory.

---

## Benchmark classes

| Class | Category | What it measures |
|---|---|---|
| `PlainTextBenchmarks` | `PlainText` | `PlainTextConverter` — small / medium / large text |
| `HtmlBenchmarks` | `Html` | `HtmlConverter` — HtmlAgilityPack + ReverseMarkdown pipeline |
| `CsvBenchmarks` | `Csv` | `CsvConverter` — CsvHelper parse + Markdown table builder, parameterised by row count (10 / 100 / 1 000) |
| `DocxBenchmarks` | `Office`, `Docx` | `DocxConverter` — OpenXml DOM walk, 5 vs 100 paragraphs |
| `XlsxBenchmarks` | `Office`, `Xlsx` | `XlsxConverter` — SpreadsheetML, 10 vs 200 rows |
| `PptxBenchmarks` | `Office`, `Pptx` | `PptxConverter` — PresentationML, 3 vs 20 slides |
| `PdfBenchmarks` | `Pdf` | `PdfConverter` — PdfPig page extraction, parameterised by page count (1 / 5 / 20) |
| `ImageBenchmarks` | `Image` | `ImageConverter` — MetadataExtractor BMP header read |
| `ZipBenchmarks` | `Zip` | `ZipConverter` — recursive conversion, 3 vs 20 entries |
| `RssBenchmarks` | `Rss` | `RssConverter` — SyndicationFeed parse, 5 vs 100 items |
| `EpubBenchmarks` | `Epub` | `EpubConverter` — VersOne.Epub parse, 2 vs 15 chapters |
| `MobiBenchmarks` | `Mobi` | `MobiConverter` — PalmDB parse + PalmDoc decode, 2 vs 10 sections |
| `ServiceBenchmarks` | `Service` | Full end-to-end pipeline across all major formats (dispatch + conversion) side-by-side |

---

## Test data

All test data is generated **in-memory** by `TestData/TestDataFactory.cs` during each benchmark class's `[GlobalSetup]`. No files are read from disk.

| Format | How generated |
|---|---|
| Plain text, HTML, CSV, RSS | StringBuilder → UTF-8 bytes |
| DOCX | `WordprocessingDocument.Create` (DocumentFormat.OpenXml) |
| XLSX | `SpreadsheetDocument.Create` (DocumentFormat.OpenXml) |
| PPTX | `PresentationDocument.Create` (DocumentFormat.OpenXml) |
| PDF | Programmatically assembled PDF-1.4 with correct xref byte offsets |
| Image (BMP) | Hardcoded 1×1 white BMP (58 bytes) |
| ZIP | `ZipArchive` with plain-text entries |
| EPUB | `ZipArchive` with EPUB 2 container structure |
| MOBI | PalmDB binary assembled in-memory with PalmDoc header and uncompressed text record |

---

## Diagnosers

All benchmark classes are annotated with `[MemoryDiagnoser]`, which adds two extra columns to the output:

| Column | Meaning |
|---|---|
| `Alloc Ratio` | Allocated bytes relative to the baseline benchmark |
| `Allocated` | Total managed heap bytes allocated per operation |

---

## Notes

- BenchmarkDotNet is **not** compatible with Native AOT; `PublishAot` and `IsTrimmable` are disabled in this project's `.csproj`.
- The `MarkItDown.Core` project is referenced with its normal JIT configuration — benchmarks measure the JIT-compiled runtime, which matches the non-AOT development experience.
- Each benchmark method creates a **new `MemoryStream`** wrapping the pre-allocated byte array. This is O(1) and correctly models the `MarkItDownService.ConvertStreamAsync` contract (the service buffers the stream internally).
