# MarkItDown for .NET

A C#/.NET 10 port of Microsoft''s [markitdown](https://github.com/microsoft/markitdown) Python library — a converter-driven pipeline that turns virtually any document or URL into clean Markdown.

The solution ships as three focused projects: a portable core library, a Native AOT CLI (with a built-in MCP server mode), and an xUnit test suite.

---

## Architecture

<p align="center">
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 900 640" width="900" height="640" font-family="Segoe UI, Arial, sans-serif" font-size="13">
  <rect width="900" height="640" fill="#fff" rx="12"/>
  <text x="450" y="32" text-anchor="middle" font-size="16" font-weight="bold" fill="#1a1a2e">MarkItDown for .NET — Architecture</text>
  <rect x="30" y="52" width="840" height="56" rx="8" fill="#e8f4fd" stroke="#90caf9" stroke-width="1.5"/>
  <text x="450" y="70" text-anchor="middle" font-size="11" fill="#1565c0" font-weight="bold">INPUT SOURCES</text>
  <rect x="50" y="76" width="110" height="24" rx="5" fill="#bbdefb" stroke="#1976d2" stroke-width="1"/><text x="105" y="93" text-anchor="middle" fill="#0d47a1" font-size="12">Local File / Path</text>
  <rect x="175" y="76" width="110" height="24" rx="5" fill="#bbdefb" stroke="#1976d2" stroke-width="1"/><text x="230" y="93" text-anchor="middle" fill="#0d47a1" font-size="12">http/https URL</text>
  <rect x="300" y="76" width="110" height="24" rx="5" fill="#bbdefb" stroke="#1976d2" stroke-width="1"/><text x="355" y="93" text-anchor="middle" fill="#0d47a1" font-size="12">data: URI</text>
  <rect x="425" y="76" width="130" height="24" rx="5" fill="#bbdefb" stroke="#1976d2" stroke-width="1"/><text x="490" y="93" text-anchor="middle" fill="#0d47a1" font-size="12">Stream + StreamInfo</text>
  <rect x="570" y="76" width="110" height="24" rx="5" fill="#bbdefb" stroke="#1976d2" stroke-width="1"/><text x="625" y="93" text-anchor="middle" fill="#0d47a1" font-size="12">stdin (pipe)</text>
  <rect x="695" y="76" width="110" height="24" rx="5" fill="#bbdefb" stroke="#1976d2" stroke-width="1"/><text x="750" y="93" text-anchor="middle" fill="#0d47a1" font-size="12">file: URI</text>
  <defs><marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto"><path d="M0,0 L0,6 L8,3 z" fill="#666"/></marker></defs>
  <line x1="340" y1="108" x2="340" y2="135" stroke="#90caf9" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="450" y1="108" x2="450" y2="135" stroke="#90caf9" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="560" y1="108" x2="560" y2="135" stroke="#90caf9" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="270" y="135" width="360" height="76" rx="8" fill="#fff3e0" stroke="#ffb300" stroke-width="1.5"/>
  <text x="450" y="155" text-anchor="middle" font-weight="bold" fill="#e65100" font-size="13">MarkItDown.Cli</text>
  <text x="450" y="172" text-anchor="middle" fill="#bf360c" font-size="11">Native AOT exe · convert: file / URL / stdin &#x2192; Markdown</text>
  <text x="450" y="188" text-anchor="middle" fill="#bf360c" font-size="11">mcp: stdio (default) · HTTP/SSE --http --port N</text>
  <line x1="450" y1="211" x2="450" y2="232" stroke="#ffb300" stroke-width="1.5" marker-end="url(#arr)"/>
  <text x="450" y="228" text-anchor="middle" font-size="10" fill="#888">Direct API (library reference)</text>
  <rect x="270" y="232" width="360" height="100" rx="8" fill="#e8f5e9" stroke="#43a047" stroke-width="2"/>
  <text x="450" y="253" text-anchor="middle" font-weight="bold" fill="#1b5e20" font-size="14">MarkItDownService</text>
  <text x="450" y="270" text-anchor="middle" fill="#2e7d32" font-size="11">1. Buffer stream → seekable MemoryStream</text>
  <text x="450" y="285" text-anchor="middle" fill="#2e7d32" font-size="11">2. Normalize StreamInfo (MIME, extension, charset)</text>
  <text x="450" y="300" text-anchor="middle" fill="#2e7d32" font-size="11">3. Iterate converters by priority → first Accepts() wins</text>
  <text x="450" y="315" text-anchor="middle" fill="#2e7d32" font-size="11">4. Return DocumentConverterResult</text>
  <line x1="350" y1="332" x2="220" y2="365" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="550" y1="332" x2="680" y2="365" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="30" y="365" width="390" height="160" rx="8" fill="#fff8e1" stroke="#f9a825" stroke-width="1.5"/>
  <text x="225" y="383" text-anchor="middle" font-weight="bold" fill="#f57f17" font-size="12">Priority 0 — Specific Converters</text>
  <rect x="45" y="390" width="95" height="22" rx="4" fill="#ffe082" stroke="#f9a825"/><text x="93" y="406" text-anchor="middle" fill="#5d4037" font-size="11">Wikipedia</text>
  <rect x="150" y="390" width="95" height="22" rx="4" fill="#ffe082" stroke="#f9a825"/><text x="198" y="406" text-anchor="middle" fill="#5d4037" font-size="11">YouTube</text>
  <rect x="255" y="390" width="95" height="22" rx="4" fill="#ffe082" stroke="#f9a825"/><text x="303" y="406" text-anchor="middle" fill="#5d4037" font-size="11">DOCX</text>
  <rect x="45" y="420" width="95" height="22" rx="4" fill="#ffe082" stroke="#f9a825"/><text x="93" y="436" text-anchor="middle" fill="#5d4037" font-size="11">XLSX</text>
  <rect x="150" y="420" width="95" height="22" rx="4" fill="#ffe082" stroke="#f9a825"/><text x="198" y="436" text-anchor="middle" fill="#5d4037" font-size="11">PPTX</text>
  <rect x="255" y="420" width="95" height="22" rx="4" fill="#ffe082" stroke="#f9a825"/><text x="303" y="436" text-anchor="middle" fill="#5d4037" font-size="11">PDF</text>
  <rect x="45" y="450" width="95" height="22" rx="4" fill="#ffe082" stroke="#f9a825"/><text x="93" y="466" text-anchor="middle" fill="#5d4037" font-size="11">Image</text>
  <rect x="150" y="450" width="95" height="22" rx="4" fill="#ffe082" stroke="#f9a825"/><text x="198" y="466" text-anchor="middle" fill="#5d4037" font-size="11">EPUB</text>
  <text x="225" y="498" text-anchor="middle" fill="#795548" font-size="10">DocumentFormat.OpenXml · PdfPig · MetadataExtractor · VersOne.Epub</text>
  <rect x="480" y="365" width="390" height="160" rx="8" fill="#e8eaf6" stroke="#5c6bc0" stroke-width="1.5"/>
  <text x="675" y="383" text-anchor="middle" font-weight="bold" fill="#283593" font-size="12">Priority 10 — Generic Converters</text>
  <rect x="495" y="390" width="95" height="22" rx="4" fill="#c5cae9" stroke="#5c6bc0"/><text x="543" y="406" text-anchor="middle" fill="#1a237e" font-size="11">ZIP</text>
  <rect x="600" y="390" width="95" height="22" rx="4" fill="#c5cae9" stroke="#5c6bc0"/><text x="648" y="406" text-anchor="middle" fill="#1a237e" font-size="11">RSS / Atom</text>
  <rect x="705" y="390" width="95" height="22" rx="4" fill="#c5cae9" stroke="#5c6bc0"/><text x="753" y="406" text-anchor="middle" fill="#1a237e" font-size="11">CSV</text>
  <rect x="495" y="420" width="95" height="22" rx="4" fill="#c5cae9" stroke="#5c6bc0"/><text x="543" y="436" text-anchor="middle" fill="#1a237e" font-size="11">HTML</text>
  <rect x="600" y="420" width="200" height="22" rx="4" fill="#c5cae9" stroke="#5c6bc0"/><text x="700" y="436" text-anchor="middle" fill="#1a237e" font-size="11">Plain Text / MD / JSON / XML</text>
  <text x="675" y="498" text-anchor="middle" fill="#3949ab" font-size="10">CsvHelper · HtmlAgilityPack · ReverseMarkdown · System.ServiceModel.Syndication</text>
  <line x1="225" y1="525" x2="350" y2="550" stroke="#f9a825" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="675" y1="525" x2="550" y2="550" stroke="#5c6bc0" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="270" y="550" width="360" height="75" rx="8" fill="#fce4ec" stroke="#e91e63" stroke-width="1.5"/>
  <text x="450" y="572" text-anchor="middle" font-weight="bold" fill="#880e4f" font-size="13">DocumentConverterResult</text>
  <text x="450" y="590" text-anchor="middle" fill="#ad1457" font-size="11">string Markdown  ·  string? Title  ·  ToString() → Markdown</text>
  <text x="450" y="608" text-anchor="middle" fill="#ad1457" font-size="10">Custom converters: service.RegisterConverter(new MyConverter(), priority)</text>
</svg>
</p>

---

## Projects

| Project | Description |
|---|---|
| [`MarkItDown.Core`](MarkItDown.Core/README.md) | Converter library — models, 13 converters, `MarkItDownService` |
| [`MarkItDown.Cli`](MarkItDown.Cli/README.md) | Command-line tool + MCP server (`mcp` subcommand), Native AOT enabled |
| [`MarkItDown.Tests`](MarkItDown.Tests/README.md) | xUnit integration tests |

---

## Supported formats

| Format | Converter | Library |
|---|---|---|
| Plain text, Markdown, JSON, XML | `PlainTextConverter` | BCL |
| HTML / XHTML | `HtmlConverter` | HtmlAgilityPack, ReverseMarkdown |
| CSV | `CsvConverter` | CsvHelper |
| DOCX | `DocxConverter` | DocumentFormat.OpenXml |
| XLSX | `XlsxConverter` | DocumentFormat.OpenXml |
| PPTX | `PptxConverter` | DocumentFormat.OpenXml |
| PDF | `PdfConverter` | UglyToad.PdfPig |
| Images (EXIF/metadata) | `ImageConverter` | MetadataExtractor |
| ZIP (recursive) | `ZipConverter` | BCL `System.IO.Compression` |
| EPUB | `EpubConverter` | VersOne.Epub |
| RSS / Atom | `RssConverter` | System.ServiceModel.Syndication |
| Wikipedia URLs | `WikipediaConverter` | HtmlAgilityPack, ReverseMarkdown |
| YouTube URLs | `YouTubeConverter` | HtmlAgilityPack |

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

## Design notes

**Converter pipeline** — `MarkItDownService` buffers the input stream into a seekable `MemoryStream` once, then rewinds it before each `Accepts()` and `ConvertAsync()` call. Converters never need to worry about stream positioning.

**StreamInfo** — an immutable record carrying everything known about the input: `MimeType`, `Extension`, `Charset`, `FileName`, `LocalPath`, `Url`. The service normalises it (inferring MIME from extension, etc.) before passing it to converters. `Merge()` layers caller hints on top of auto-detected values; the overlay wins on conflicts.

**Priority** — lower number = checked first. Built-in specific converters use `0.0`, generic fallbacks use `10.0`. Custom converters can use any `double`.

**Native AOT** — the Core library is declared `IsTrimmable` and `IsAotCompatible`. The CLI and MCP server publish with `PublishAot=true`. Third-party libraries (PdfPig, ReverseMarkdown, CsvHelper, Syndication) emit `IL2104` trim warnings from within their own assemblies; these do not affect runtime correctness.

**HttpClient lifetime** — `MarkItDownService` implements `IDisposable`. A caller-supplied `HttpClient` is never disposed by the service. An internally-created one is disposed when the service is disposed.
