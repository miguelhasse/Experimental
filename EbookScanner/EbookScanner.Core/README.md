# EbookScanner.Core

A .NET 10 class library providing the core ebook scanning and metadata extraction pipeline. It supports PDF, EPUB, and MOBI files and can format results as Markdown or JSON.

## Namespace Structure

```
EbookScanner.Core
├── Models/
│   ├── BookMetadata       — unified metadata record for any ebook format
│   ├── ScanOptions        — input parameters for a directory scan
│   ├── ScanResult         — output: list of books + scan summary
│   └── BookFormat         — enum: Pdf | Epub | Mobi
├── Extractors/
│   ├── BookMetadataExtractor   — abstract base class
│   ├── PdfMetadataExtractor    — extracts from PDF via UglyToad.PdfPig
│   ├── EpubMetadataExtractor   — extracts from EPUB via VersOne.Epub
│   └── MobiMetadataExtractor   — extracts from MOBI/AZW via custom binary parser
├── Formatters/
│   ├── IMetadataFormatter      — interface
│   ├── MarkdownFormatter       — renders a human-readable catalog
│   └── JsonFormatter           — serializes ScanResult to indented JSON
└── EbookScannerService         — orchestrates scanning and extraction
```

## Models

### `BookMetadata`

```csharp
public record BookMetadata(
    string FilePath,
    string FileName,
    string Format,           // "PDF", "EPUB", or "MOBI"
    long FileSizeBytes,
    string? Title = null,
    string[]? Authors = null,
    string? Publisher = null,
    string? Description = null,
    string? Language = null,
    string? Isbn = null,
    DateTimeOffset? PublishedDate = null,
    DateTimeOffset? ModifiedDate = null,
    int? PageCount = null,   // PDF only
    string[]? Tags = null);
```

### `ScanOptions`

```csharp
public record ScanOptions(
    string Directory,
    bool Recursive = false,           // default: top-level only
    BookFormat[]? Formats = null);    // null = all formats
```

### `ScanResult`

```csharp
public record ScanResult(
    string ScannedDirectory,
    DateTimeOffset ScannedAt,
    IReadOnlyList<BookMetadata> Books);
```

## EbookScannerService

```csharp
var service = new EbookScannerService(
    onError: (file, ex) => Console.Error.WriteLine($"warning: {file}: {ex.Message}"));

// Scan a directory
var result = await service.ScanAsync(new ScanOptions("/path/to/library", Recursive: true));

// Extract metadata from a single file
var metadata = await service.ExtractAsync("/path/to/book.pdf");
```

The `onError` callback is invoked for files that fail to extract (e.g., corrupt or password-protected). Extraction continues for remaining files.

## Formatters

```csharp
IMetadataFormatter formatter = new MarkdownFormatter();
// or
IMetadataFormatter formatter = new JsonFormatter();

string output = formatter.Format(result);
```

## Extractor Details

### PDF (`PdfMetadataExtractor`)

Uses **UglyToad.PdfPig**. Extracts from the PDF Information Dictionary:

| Metadata | Source field |
|----------|-------------|
| Title | `Information.Title` |
| Authors | `Information.Author` (split by `,` or `;`) |
| Publisher | `Information.Creator` |
| Description | `Information.Subject` |
| Tags | `Information.Keywords` (split by `,` or `;`) |
| Published | `Information.CreationDate` (PDF date format) |
| Modified | `Information.ModifiedDate` (PDF date format) |
| Pages | `NumberOfPages` |

### EPUB (`EpubMetadataExtractor`)

Uses **VersOne.Epub**. Reads from the OPF package metadata:

| Metadata | OPF element |
|----------|------------|
| Title | `dc:title` |
| Authors | `dc:creator` (role = aut) |
| Publisher | `dc:publisher` |
| Description | `dc:description` |
| Language | `dc:language` |
| ISBN | `dc:identifier` with scheme containing "isbn" |
| Published | `dc:date` (event = publication/issued) |
| Tags | `dc:subject` |

### MOBI (`MobiMetadataExtractor`)

No external library. Parses the **Palm Database Format** binary structure:

- PalmDB header (bytes 0–31): database name as fallback title
- MOBI header: full title string offset/length
- **EXTH block**: richest metadata, read record by record

Key EXTH record types used:

| Type | Field |
|------|-------|
| 100 | Author(s) — multiple records allowed |
| 101 | Publisher |
| 103 | Description |
| 104 | ISBN |
| 105 | Tags/Subjects — multiple records allowed |
| 106 | Published date |
| 503 | Updated title (preferred) |
| 524 | Language |

Handles truncated or malformed files by falling back to the Palm DB name as title.

## Dependencies

```xml
<PackageReference Include="UglyToad.PdfPig" />
<PackageReference Include="VersOne.Epub" />
```

## AOT Compatibility

This library is fully trim- and AOT-compatible:

- `<IsTrimmable>true</IsTrimmable>` — safe to link-trim when publishing
- `<IsAotCompatible>true</IsAotCompatible>` — no reflection at runtime

JSON serialization is handled through a **source-generated** `JsonSerializerContext` rather than reflection:

```csharp
// EbookScannerJsonContext.cs
[JsonSourceGenerationOptions(WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ScanResult))]
[JsonSerializable(typeof(BookMetadata))]
public partial class EbookScannerJsonContext : JsonSerializerContext { }
```

`JsonFormatter` and the CLI's `ExtractMetadata` handler both use this context instead of `JsonSerializerOptions`.
