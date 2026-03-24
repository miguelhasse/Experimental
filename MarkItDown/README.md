# MarkItDown for .NET

A C#/.NET 10 port of Microsoft''s [markitdown](https://github.com/microsoft/markitdown) Python library — a converter-driven pipeline that turns virtually any document or URL into clean Markdown.

The solution ships as three focused projects: a portable core library, a Native AOT CLI (with a built-in MCP server mode), and an xUnit test suite.

---

## Architecture

![architecture diagram](/assets/diagrams/markitdown.svg)

---

## Projects

| Project | Description |
|---|---|
| [`MarkItDown.Core`](MarkItDown.Core/README.md) | Converter library — models, 14 converters, `MarkItDownService` |
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
| MOBI / AZW | `MobiConverter` | *(built-in parser — no extra dependency)* |
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
