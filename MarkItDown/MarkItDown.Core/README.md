# MarkItDown.Core

The portable conversion library at the heart of the solution. It exposes `MarkItDownService`, 15 built-in document converters, and all the models needed to add custom converters.

**Target:** `net10.0` · **Nullable:** enabled · **Trimming / AOT:** `IsTrimmable=true`, `IsAotCompatible=true`

---

## Package dependencies

| Package | Version | Purpose |
|---|---|---|
| `DocumentFormat.OpenXml` | 3.5.1 | DOCX, XLSX, PPTX parsing |
| `CsvHelper` | 33.1.0 | CSV parsing |
| `HtmlAgilityPack` | 1.12.4 | HTML DOM parsing |
| `ReverseMarkdown` | 5.2.0 | HTML → Markdown conversion |
| `MetadataExtractor` | 2.9.2 | Image EXIF / metadata |
| `UglyToad.PdfPig` | 1.7.0-custom-5 | PDF text extraction |
| `VersOne.Epub` | 3.3.6 | EPUB parsing |
| `System.ServiceModel.Syndication` | 10.0.5 | RSS / Atom feeds |

---

## Public API

### `MarkItDownService`

The main entry point. Implements `IDisposable`.

```csharp
// Create with a shared HttpClient (recommended in server scenarios)
var service = new MarkItDownService(httpClient);

// Or let the service manage its own HttpClient
using var service = new MarkItDownService();
```

#### Conversion methods

```csharp
// Smart dispatch: detects URI vs. local path automatically
Task<DocumentConverterResult> ConvertAsync(
    string source,
    StreamInfo? streamInfo = null,
    CancellationToken cancellationToken = default)

// Always treats the string as a local file path
Task<DocumentConverterResult> ConvertLocalAsync(
    string path,
    StreamInfo? streamInfo = null,
    CancellationToken cancellationToken = default)

// Handles http://, https://, file://, and data: URIs
Task<DocumentConverterResult> ConvertUriAsync(
    Uri uri,
    StreamInfo? streamInfo = null,
    CancellationToken cancellationToken = default)

// Convert from any stream; streamInfo provides hints
Task<DocumentConverterResult> ConvertStreamAsync(
    Stream stream,
    StreamInfo? streamInfo = null,
    CancellationToken cancellationToken = default)
```

#### Converter registration

```csharp
// Register a custom converter. Lower priority = checked first.
void RegisterConverter(DocumentConverter converter, double priority = 0.0)

// Inspect the registered converters (sorted by priority, cached)
IReadOnlyList<ConverterRegistration> Converters { get; }
```

#### Priority constants

```csharp
MarkItDownService.PrioritySpecific = 0.0   // for format-specific converters
MarkItDownService.PriorityGeneric  = 10.0  // for fallback converters
```

---

### `DocumentConverterResult`

```csharp
public sealed class DocumentConverterResult
{
    public string  Markdown { get; }   // never null; empty string if nothing to output
    public string? Title    { get; }   // extracted document title, or null
    public override string ToString() => Markdown;
}
```

---

### `StreamInfo`

An immutable record. All properties are optional — fill in what you know:

```csharp
var info = new StreamInfo(
    MimeType:  "application/pdf",
    Extension: ".pdf",
    Charset:   null,
    FileName:  "report.pdf",
    LocalPath: @"C:\docs\report.pdf",
    Url:       null);
```

`Merge(StreamInfo? other)` — combines two `StreamInfo` values. The *other* value wins on conflicts (non-null properties in `other` replace those in `this`).

The service normalises `StreamInfo` before passing it to converters: it infers `MimeType` from `Extension` when absent, infers `Extension` from `FileName` or `LocalPath`, and sets `Charset` to `utf-8` for known text MIME types.

---

### `DocumentConverter` (abstract base)

Subclass this to add custom converters:

```csharp
public abstract class DocumentConverter
{
    public virtual string Name => GetType().Name;   // used in error messages

    // Return true if this converter can attempt the input.
    // The stream is seekable; do NOT advance its position.
    public abstract bool Accepts(Stream stream, StreamInfo streamInfo);

    // Perform the conversion. The stream is positioned at offset 0.
    // Throw UnsupportedFormatException to fall through to the next converter.
    public abstract Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default);
}
```

#### `MarkItDownConversionContext`

Passed to every converter's `ConvertAsync`. Provides:

```csharp
MarkItDownService Service    { get; }  // for recursive conversion (e.g. ZipConverter)
HttpClient        HttpClient { get; }  // for converters that need to make HTTP requests
```

---

### Exceptions

| Type | When thrown |
|---|---|
| `UnsupportedFormatException` | No converter accepted the input, or a converter voluntarily falls through |
| `FileConversionException` | An accepting converter threw an unexpected error (wraps the inner exception) |

Both derive from `FileConversionException`. `UnsupportedFormatException` is sealed; `FileConversionException` can be subclassed.

---

## Built-in converters

### Priority 0 (specific — checked first)

| Converter | Accepts | Notes |
|---|---|---|
| `WikipediaConverter` | URLs with `wikipedia.org` host | Full article HTML → Markdown via `HtmlMarkdownConverter` |
| `YouTubeConverter` | `youtube.com` / `youtu.be` URLs | Extracts `og:title`, `og:description`, page title via meta tags |
| `DocxConverter` | `.docx`, OOXML MIME | Headings 1–6, paragraphs, list items (bullet), tables |
| `XlsxConverter` | `.xlsx`, OOXML MIME | Each sheet → `## SheetName` + Markdown table; handles shared strings |
| `PptxConverter` | `.pptx`, OOXML MIME | Each slide → comment block + title + body text + tables + notes |
| `PdfConverter` | `.pdf`, `application/pdf` | Page-by-page plain text extraction |
| `ImageConverter` | Common image extensions + `image/*` MIME | Full EXIF / metadata dump as Markdown lists |
| `EpubConverter` | `.epub`, `application/epub+zip` | Reading-order chapters → HTML → Markdown |
| `MobiConverter` | `.mobi`, `.azw`, `application/x-mobipocket-ebook`, `application/x-mobi8-ebook` | PalmDB container + PalmDoc decompression → HTML → Markdown; title from MOBI FullName or PalmDB name |
| `ChmConverter` | `.chm`, `application/vnd.ms-htmlhelp`, ITSF magic | ITSF binary container + LZX decompression → HTML → Markdown; title from `#SYSTEM`; TOC order from HHC |

### Priority 10 (generic — fallback chain)

| Converter | Accepts | Notes |
|---|---|---|
| `ZipConverter` | `.zip`, `application/zip` | Recursively converts each entry via the service; sanitises path traversal |
| `RssConverter` | RSS/Atom MIME or `.rss`/`.atom` extension | Feed title + description + items (title, link, summary) |
| `CsvConverter` | `.csv`, `text/csv`, `application/csv` | First row is the header; all cells pipe-escaped |
| `HtmlConverter` | `.html`/`.htm`, `text/html`, `application/xhtml` | Strips `<script>` and `<style>`, converts body via ReverseMarkdown |
| `PlainTextConverter` | Text extensions + `text/*` / `application/json` / `application/xml` MIMEs | Charset-aware decoding; rejects known binary MIMEs even if charset is set |

---

## Utilities (internal)

| Class | Purpose |
|---|---|
| `MimeHelpers` | Extension ↔ MIME mapping, `Normalize(StreamInfo)`, `GetFileNameFromUrl` |
| `StreamHelpers` | `BufferAsync` (copy to seekable `MemoryStream`), `ReadAllTextAsync`, charset detection with BOM + UTF-8 probe + Latin-1 fallback |
| `MarkdownHelpers` | `BuildTable(rows)` with pipe-escaping, `EscapeInline` |
| `HtmlMarkdownConverter` | Shared HTML → `DocumentConverterResult` pipeline used by HTML, Wikipedia, EPUB, and MOBI converters |
| `PalmDocDecoder` | PalmDoc LZ77 decompressor used by `MobiConverter`; supports compression types 1 (none) and 2 (LZ77) |
| `ChmParser` | ITSF/ITSP binary parser used by `ChmConverter`; walks PMGL B-tree, reads `#SYSTEM` title, exposes section-0 (uncompressed) and section-1 (LZX) files |
| `LzxDecoder` | CHM-variant LZX sliding-window decompressor used by `ChmParser`; pure algorithmic, AOT-safe |

---

## Adding a custom converter

```csharp
using MarkItDown.Core;
using MarkItDown.Core.Converters;
using MarkItDown.Core.Models;

public sealed class MyConverter : DocumentConverter
{
    public override bool Accepts(Stream stream, StreamInfo streamInfo)
        => streamInfo.Extension is ".myext"
        || string.Equals(streamInfo.MimeType, "application/x-myformat",
               StringComparison.OrdinalIgnoreCase);

    public override async Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        // stream is positioned at 0 and is seekable
        using var reader = new StreamReader(stream, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken);
        var markdown = $"# Converted\n\n```\n{text}\n```";
        return new DocumentConverterResult(markdown, title: "My Document");
    }
}

// Register at priority 0 so it runs before generic converters
using var service = new MarkItDownService();
service.RegisterConverter(new MyConverter(), priority: 0.0);
```

---

## Dependency injection (ASP.NET Core / Generic Host)

```csharp
builder.Services.AddHttpClient();   // or use IHttpClientFactory
builder.Services.AddSingleton<MarkItDownService>(sp =>
    new MarkItDownService(sp.GetRequiredService<IHttpClientFactory>()
                            .CreateClient("markitdown")));
```

`MarkItDownService` is thread-safe after construction (converters are registered in the constructor). Register it as a **singleton**.
