# MarkItDown for .NET

A C#/.NET 10 port of Microsoft's [markitdown](https://github.com/microsoft/markitdown) Python library — a converter-driven pipeline that turns virtually any document or URL into clean Markdown.

The solution ships as three focused projects: a portable core library, a Native AOT CLI (with a built-in MCP server mode), and an xUnit test suite.  
**70/70 tests passing. Matches or exceeds the upstream Python implementation for every shared format.**

---

## Architecture

![architecture diagram](/assets/diagrams/markitdown.svg)

---

## Projects

| Project | Description |
|---|---|
| [`MarkItDown.Core`](MarkItDown.Core/README.md) | Converter library — models, 15 converters, `MarkItDownService` |
| [`MarkItDown.Cli`](MarkItDown.Cli/README.md) | Command-line tool + MCP server (`mcp` subcommand), Native AOT enabled |
| [`MarkItDown.Tests`](MarkItDown.Tests/README.md) | xUnit integration tests — 70 tests across all formats |

---

## Supported formats

| Format | Converter | Library | Notes |
|---|---|---|---|
| Plain text, Markdown, JSON, XML | `PlainTextConverter` | BCL | Charset-aware; rejects known binary MIMEs |
| HTML / XHTML | `HtmlConverter` | HtmlAgilityPack, ReverseMarkdown | Strips scripts/styles; converts body |
| CSV | `CsvConverter` | CsvHelper | Multi-encoding heuristic (UTF-8, Shift-JIS, GBK, Big5, EUC-KR, EUC-JP, Latin-1) |
| DOCX | `DocxConverter` | DocumentFormat.OpenXml | Headings 1–6 (incl. custom style inheritance), bold/italic, tables, embedded image data URIs |
| XLSX | `XlsxConverter` | DocumentFormat.OpenXml | Each sheet → `## Name` + GFM table; handles sparse cells and shared strings |
| PPTX | `PptxConverter` | DocumentFormat.OpenXml | Slides with title, body, tables, charts, image alt-text, speaker notes |
| PDF | `PdfConverter` | UglyToad.PdfPig | Layout-aware: bounding-box word sort → line groups → paragraph gap detection |
| Images (EXIF/metadata) | `ImageConverter` | MetadataExtractor | Full EXIF/IPTC/XMP grouped by directory; no external binary needed |
| ZIP (recursive) | `ZipConverter` | BCL `System.IO.Compression` | Recursively converts each entry; path traversal sanitised |
| EPUB | `EpubConverter` | VersOne.Epub | Reading-order chapters with `## Chapter Title` headings from NCX/NAV navigation |
| MOBI / AZW | `MobiConverter` | *(built-in parser)* | PalmDB + PalmDoc decompression → HTML → Markdown; magic-byte detection |
| CHM (Compiled HTML Help) | `ChmConverter` | *(built-in ITSF parser)* | ITSF/ITSP + LZX decompression → HTML → Markdown; TOC order from HHC |
| RSS / Atom | `RssConverter` | System.ServiceModel.Syndication | Feed + item Markdown links, RFC 1123 dates, HTML-stripped summaries |
| Wikipedia URLs | `WikipediaConverter` | HtmlAgilityPack, ReverseMarkdown | `mw-content-text` targeting, `mw-page-title-main` title, `# Title` prefix |
| YouTube URLs | `YouTubeConverter` | HtmlAgilityPack | `og:title`, `og:description`, canonical URL |

---

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (pinned in `global.json`)
- Native AOT publish additionally requires the platform native C++ toolchain (MSVC on Windows, `clang` on Linux/macOS)

---

## Build

```powershell
dotnet build MarkItDown.slnx
```

## Test

```powershell
dotnet test MarkItDown.slnx
```

## Publish as Native AOT

```powershell
dotnet publish MarkItDown.Cli -c Release
```

Produces a self-contained native executable with no .NET runtime dependency.

| Binary | Approx. size |
|---|---|
| `MarkItDown.Cli.exe` | ~65 MB |

---

## Quick start — CLI

```powershell
# Convert a local file
dotnet run --project MarkItDown.Cli -- report.pdf

# Save to file
dotnet run --project MarkItDown.Cli -- report.docx -o report.md

# Pipe from stdin (hint the format via --input-name or --mime-type)
Get-Content page.html | dotnet run --project MarkItDown.Cli -- --input-name page.html

# Convert a URL
dotnet run --project MarkItDown.Cli -- https://en.wikipedia.org/wiki/Markdown

# Use the AOT binary directly
.\MarkItDown.Cli.exe report.xlsx -o report.md
```

## Quick start — MCP server

```powershell
# stdio transport (default — for MCP client integration)
dotnet run --project MarkItDown.Cli -- mcp

# HTTP transport
dotnet run --project MarkItDown.Cli -- mcp --http --port 3001
```

The server exposes one tool: **`convert_to_markdown`** — accepts a `file:`, `data:`, `http:`, or `https:` URI and returns Markdown text.

## Quick start — Library

```csharp
using MarkItDown.Core;
using MarkItDown.Core.Models;

using var service = new MarkItDownService();

// From a file path or URL
var result = await service.ConvertAsync("report.pdf");
var wiki   = await service.ConvertAsync("https://en.wikipedia.org/wiki/Markdown");

// From a stream
await using var stream = File.OpenRead("data.csv");
var csv = await service.ConvertStreamAsync(stream, new StreamInfo(Extension: ".csv"));

Console.WriteLine(result.Markdown);
Console.WriteLine(result.Title);   // null if not available
```

### Custom converters

```csharp
using var service = new MarkItDownService();
service.RegisterConverter(new MyCustomConverter(), priority: 0.0);
```

Converters are tried in ascending priority order. The first one whose `Accepts()` returns `true` handles the conversion. Throwing `UnsupportedFormatException` inside `ConvertAsync` falls through to the next candidate.

---

## Comparison with Python upstream

The C# port targets feature parity with [microsoft/markitdown](https://github.com/microsoft/markitdown). The table below summarises the current state for every format shared by both implementations.

| Format | Parity | Notes |
|---|---|---|
| Plain text / JSON / XML | ✅ Full | C# supports broader MIME/extension matching |
| HTML / XHTML | ✅ Full | Different libraries; equivalent output for LLM consumption |
| CSV | ✅ Exceeds | Round-trip fidelity heuristic; strict declared-charset validation — upstream uses `charset_normalizer` |
| XLSX | ✅ Full | C# handles sparse cells correctly; upstream routes through pandas HTML |
| DOCX | ✅ Exceeds | C# adds: custom heading style inheritance, inline bold/italic, image data URIs |
| PPTX | ✅ Full | Identical output structure (slide comments, title `#`, notes, tables, charts) |
| PDF | ✅ Near-full | Both layout-aware; upstream also extracts inline figures (pdfminer `LTFigure`) |
| EPUB | ✅ Exceeds | C# emits `## Chapter Title` headings from NCX/NAV navigation; upstream does not |
| RSS / Atom | ✅ Exceeds | C# emits Markdown links for feed and items, normalised RFC 1123 dates |
| Images | ✅ Exceeds | C# supports more formats (GIF, BMP, TIFF, WebP) and requires no external binary |
| ZIP | ✅ Full | C# adds path-traversal sanitisation |
| YouTube | ✅ Full | Upstream additionally extracts transcripts via `youtube_transcript_api` |
| Wikipedia | ✅ Full | Exact match — `mw-content-text`, `mw-page-title-main`, `# Title\n\n` heading prefix |
| MOBI / AZW | 🆕 C# only | No upstream equivalent |
| CHM | 🆕 C# only | No upstream equivalent |

### Upstream-only features (not ported)

| Feature | Reason |
|---|---|
| XLS (legacy Excel) | Old binary format; `xlrd` has no pure-.NET equivalent |
| Jupyter Notebook (`.ipynb`) | Niche; JSON cells with embedded output |
| Outlook MSG | MAPI/CFBF compound binary — complex, rarely needed |
| Audio transcription | Requires Whisper ML model |
| Bing SERP | Specific HTML selectors for Bing search result pages |
| Azure Document Intelligence | Cloud API, premium feature — out of scope |
| LLM image/slide captions | `llm_client`/`llm_model` hook — optional enhancement |
| PPTX data URI images | `keep_data_uris=True` option — not yet ported |

### Performance characteristics

| Characteristic | Python upstream | C# port |
|---|---|---|
| Startup time | Slow (interpreter + heavy imports) | Fast (AOT native binary) |
| Regex allocation | `re.compile()` at import time | `[GeneratedRegex]` — zero allocation per call |
| Binary dependencies | exiftool (optional) | None — all parsers are pure .NET |
| Trimming / AOT | N/A | `IsTrimmable=true`, `IsAotCompatible=true` on Core |
| Plugin system | `#markitdown-plugin` via PyPI | Not implemented |

---

## Design notes

**Converter pipeline** — `MarkItDownService` buffers the input stream into a seekable `MemoryStream` once, then rewinds it before each `Accepts()` and `ConvertAsync()` call. Converters never need to worry about stream positioning.

**StreamInfo** — an immutable record carrying everything known about the input: `MimeType`, `Extension`, `Charset`, `FileName`, `LocalPath`, `Url`. The service normalises it (inferring MIME from extension, etc.) before passing it to converters. `Merge()` layers caller hints on top of auto-detected values; the overlay wins on conflicts.

**Priority** — lower number = checked first. Built-in specific converters use `0.0`, generic fallbacks use `10.0`. Custom converters can use any `double`.

**Native AOT** — the Core library is declared `IsTrimmable` and `IsAotCompatible`. The CLI and MCP server publish with `PublishAot=true`. Third-party libraries (PdfPig, ReverseMarkdown, CsvHelper, Syndication) emit `IL2104` trim warnings from within their own assemblies; these do not affect runtime correctness.

**HttpClient lifetime** — `MarkItDownService` implements `IDisposable`. A caller-supplied `HttpClient` is never disposed by the service. An internally-created one is disposed when the service is disposed.
