# MarkItDown.Tests

xUnit integration tests for the `MarkItDown.Core` library.

**Target:** `net10.0` · **Framework:** xUnit 2.9 · **Runner:** `xunit.runner.visualstudio`

---

## Run

```powershell
dotnet test MarkItDown.Tests
```

Or from the solution root:

```powershell
dotnet test MarkItDown.slnx
```

---

## Test coverage — 70 tests, all passing

### Core service / models

| Test | Description |
|---|---|
| `PlainTextConversion_ReturnsSourceText` | `PlainTextConverter` returns the original text unchanged |
| `PlainText_JsonExtension_IsAccepted` | `.json` extension is handled by `PlainTextConverter` |
| `PlainText_BinaryMime_NotAcceptedAsText` | `PlainTextConverter` rejects `application/pdf` (binary MIME guard) |
| `UnsupportedFormat_ThrowsUnsupportedFormatException` | Unknown stream throws `UnsupportedFormatException` |
| `StreamInfo_Merge_OverlayTakesPrecedence` | Non-null overlay properties win over base values |
| `StreamInfo_Merge_PreservesExistingValues` | Null overlay properties do not overwrite base values |
| `Converters_Property_ReturnsSortedByPriority` | `MarkItDownService.Converters` is sorted ascending by priority |
| `Service_ImplementsIDisposable` | `MarkItDownService.Dispose()` does not throw |

### HTML

| Test | Description |
|---|---|
| `HtmlConversion_ProducesMarkdown` | `<h1>` and `<p>` tags convert to Markdown heading and paragraph |
| `EmptyHtml_ReturnsEmptyMarkdown` | HTML with no body text returns empty (or whitespace-only) Markdown |
| `DataUri_TextPlain_IsConverted` | `data:text/plain;base64,...` URI round-trips correctly |

### CSV

| Test | Description |
|---|---|
| `CsvConversion_ProducesMarkdownTable` | First row becomes header; all rows produce a GFM pipe table |
| `CsvWithSpecialChars_EscapesPipeInCells` | Pipe characters `\|` in cells are escaped to `\|` |
| `CsvConversion_HandlesShiftJISEncoding` | Shift-JIS encoded CSV is decoded correctly via round-trip heuristic |

### XLSX

| Test | Description |
|---|---|
| `XlsxConversion_ProducesMarkdownTable` | Sheet rows produce a GFM pipe table |
| `XlsxConversion_IncludesSheetName` | Output includes `## SheetName` heading |

### DOCX

| Test | Description |
|---|---|
| `DocxConversion_ProducesParagraphText` | Body paragraphs appear in output |
| `DocxConversion_ProducesHeadingMarkdown` | `Heading1` style produces `# Heading` |
| `DocxConversion_ExtractsDocumentTitle` | `PackageProperties.Title` is populated in result |
| `DocxConversion_EmitsBoldMarkdown` | Bold runs produce `**text**` |
| `DocxConversion_EmitsItalicMarkdown` | Italic runs produce `_text_` |
| `DocxConversion_EmitsBoldItalicMarkdown` | Bold+italic runs produce `**_text_**` |
| `DocxConversion_UsesOutlineLevelForHeadings` | `OutlineLevel` attribute maps to correct `#` depth |
| `DocxConversion_ResolvesCustomStyleInheritanceToHeading` | Custom style based on `Heading2` resolves to `##` via `basedOn` chain |
| `DocxConversion_EmitsEmbeddedImageMarkdown` | Embedded image produces `data:image/...;base64,...` reference |

### PPTX

| Test | Description |
|---|---|
| `PptxConversion_ExtractsTitleShape` | Title placeholder produces `# Title` heading |
| `PptxConversion_IncludesSlideNumberComment` | Each slide starts with `<!-- Slide number: N -->` |
| `PptxConversion_HandlesImagesChartsAndGroupedShapes` | Complex slide with images, chart data, and group shapes converts without error |

### PDF

| Test | Description |
|---|---|
| `PdfConversion_ProducesPageSectionHeading` | Each page produces `## Page N` heading |

### EPUB

| Test | Description |
|---|---|
| `EpubConversion_ExtractsBookTitle` | Book title appears in metadata block |
| `EpubConversion_ProducesMarkdownFromChapterContent` | Chapter HTML is converted to Markdown |
| `EpubConverterParityTests.EpubConversion_UsesNavTitleAsHeading` | NCX/NAV title emitted as `## Chapter Title` |
| `EpubConverterParityTests.EpubConversion_DoesNotEmitRawXhtmlFilenameAsHeading` | Raw filename never used as heading when nav title exists |
| `EpubConverterParityTests.EpubConversion_PrependsMetadataBeforeChapterContent` | Metadata block precedes chapter content |

### RSS / Atom

| Test | Description |
|---|---|
| `RssConversion_ProducesMarkdownWithFeedTitleAndItems` | Feed title → `# Title`; items appear in output |
| `RssConversion_FormatsLinkAsMarkdown` | Feed + item links rendered as `[Title](url)` Markdown links |
| `RssConversion_EmitsPublishedDate` | Item publish date appears as `Published on: {RFC 1123}` |
| `RssConversion_StripsHtmlFromDescription` | HTML tags stripped from item summaries |
| `RssConversion_AtomFeed_ProducesMarkdown` | Atom feed converts equivalently to RSS |
| `RssConversion_XmlMimeType_IsDetectedByContent(text/xml)` | `text/xml` MIME triggers RSS detection from feed content |
| `RssConversion_XmlMimeType_IsDetectedByContent(application/xml)` | `application/xml` MIME triggers RSS detection |
| `RssConversion_XmlExtension_IsDetectedByContent` | `.xml` extension triggers RSS detection when content is a feed |

### Images

| Test | Description |
|---|---|
| `ImageConversion_BmpProducesMetadataMarkdown` | BMP file produces grouped EXIF metadata Markdown |

### ZIP

| Test | Description |
|---|---|
| `ZipConversion_ExtractsTextFileContent` | Text entry content appears in output |
| `ZipConversion_SkipsUnsupportedEntries` | Entries with unknown format are silently skipped |

### YouTube

| Test | Description |
|---|---|
| `YouTubeConversion_ExtractsTitleAndDescription` | `og:title` and `og:description` appear in output |
| `YouTubeConverter_AcceptsYoutubeComUrl` | `youtube.com` URL accepted |
| `YouTubeConverter_AcceptsYoutuBeShortUrl` | `youtu.be` short URL accepted |
| `YouTubeConverter_RejectsNonYoutubeUrl` | Non-YouTube URL rejected |

### Wikipedia

| Test | Description |
|---|---|
| `WikipediaConverter_AcceptsWikipediaUrl` | `en.wikipedia.org` URL accepted |
| `WikipediaConverter_RejectsNonWikipediaUrl` | Non-Wikipedia URL rejected |
| `WikipediaConverter_RejectsSpoof_NotWikipediaOrg` | Spoofed domain (`fakewikipedia.org`) rejected |
| `WikipediaConversion_ProducesMarkdownFromHtmlStream` | HTML stream with `mw-content-text` div produces Markdown |
| `WikipediaConversion_ExtractsMainContent` | Navigation chrome excluded; article body included |
| `WikipediaConversion_PrependsTitleHeading` | `mw-page-title-main` span produces `# Title` heading |
| `WikipediaConversion_FallsBackOnGenericHtml` | HTML without `mw-content-text` falls back to generic conversion |

### MOBI / AZW

| Test | Description |
|---|---|
| `MobiConverter_AcceptsMobiExtension` | `.mobi` extension accepted |
| `MobiConverter_AcceptsAzwExtension` | `.azw` extension accepted |
| `MobiConverter_AcceptsMobiMimeType` | `application/x-mobipocket-ebook` MIME accepted |
| `MobiConversion_ExtractsBookTitle` | MOBI FullName field surfaced as document title |
| `MobiConversion_ProducesMarkdownFromContent` | PalmDoc decompressed content converts to Markdown |

### CHM

| Test | Description |
|---|---|
| `ChmConverter_AcceptsByChmExtension` | `.chm` extension accepted |
| `ChmConverter_AcceptsByMimeType` | `application/vnd.ms-htmlhelp` MIME accepted |
| `ChmConverter_AcceptsByItsfMagicBytes` | ITSF magic bytes at offset 0 accepted |
| `ChmConverter_RejectsNonChmFile` | Non-CHM stream rejected |
| `ChmRealFile_SqlServerSystemTablesMap_ProducesNonEmptyMarkdown` | Real CHM file converts to non-empty Markdown |
| `ChmRealFile_MicrosoftDotNetRemoting_ProducesNonEmptyMarkdown` | Real CHM file (complex LZX) converts correctly |
| `ChmRealFile_DeployingDotNetApplications_ProducesNonEmptyMarkdown` | Real CHM file with HHC TOC converts correctly |

---

## Extending the tests

1. Add test methods to `ConverterTests.cs`, or create new `*Tests.cs` files in the project.
2. Use the shared `MarkItDownService` setup in the test class constructor, or instantiate the service directly in individual tests.
3. For format-specific tests, place sample files in a `TestData/` folder and set `<CopyToOutputDirectory>Always</CopyToOutputDirectory>` in the `.csproj`, then load them with `File.OpenRead(Path.Combine("TestData", "sample.pdf"))`.

```csharp
[Fact]
public async Task MyConverter_WorksCorrectly()
{
    using var service = new MarkItDownService();
    await using var stream = File.OpenRead(Path.Combine("TestData", "sample.myext"));
    var result = await service.ConvertStreamAsync(stream,
        new StreamInfo(Extension: ".myext"));
    Assert.Contains("expected content", result.Markdown);
}
```
