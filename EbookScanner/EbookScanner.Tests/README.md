# EbookScanner.Tests

Unit test project for EbookScanner using **xUnit.v3**.

## Running Tests

```bash
# From the solution root
dotnet test EbookScanner.Tests/EbookScanner.Tests.csproj

# With verbose output
dotnet test EbookScanner.Tests/EbookScanner.Tests.csproj --logger "console;verbosity=normal"

# Run all tests in the solution
dotnet test EbookScanner.slnx
```

## Test Structure

```
EbookScanner.Tests/
├── Formatters/
│   ├── MarkdownFormatterTests.cs      — catalog output, field formatting, edge cases (6 tests)
│   ├── MarkdownTableFormatterTests.cs — table output, column selection, escaping (10 tests)
│   └── JsonFormatterTests.cs          — JSON structure, camelCase, null omission (5 tests)
├── Extractors/
│   ├── MobiMetadataExtractorTests.cs  — extension acceptance, binary parsing (13 tests)
│   └── ChmMetadataExtractorTests.cs   — extension acceptance, ITSF binary parsing (13 tests)
└── EbookScannerServiceTests.cs        — directory scanning, recursion, format filtering (13 tests)
```

## Test Coverage

### `MarkdownFormatterTests`

| Test | Description |
|------|-------------|
| `Format_EmptyResult_ContainsCatalogHeader` | Verifies header and directory are included |
| `Format_SingleBook_ContainsTitle` | Book title appears as a section heading |
| `Format_SingleBook_ContainsAllFields` | Authors, publisher, ISBN, pages, date, tags, size |
| `Format_MultipleBooks_ShowsFormatBreakdown` | PDF/EPUB/MOBI/CHM counts in summary line |
| `Format_BookWithNoTitle_UsesFileNameWithoutExtension` | Fallback title from filename |
| `Format_LongDescription_IsTruncated` | Descriptions over 200 chars are truncated |

### `MarkdownTableFormatterTests`

| Test | Description |
|------|-------------|
| `Format_DefaultColumns_SixColumns` | Default column set has six headers |
| `Format_DefaultColumns_DataRowContainsValues` | Data row values match the book |
| `Format_EmptyResult_ContainsHeaderAndEmptyTable` | Table with no data rows |
| `Format_MultipleBooks_MultipleDataRows` | One data row per book |
| `Format_CustomColumns_OnlyRequestedColumnsPresent` | Column subset selection |
| `Format_AllColumns_AllHeadersPresent` | All 13 valid columns appear |
| `Format_NoTitle_UsesFilenameWithoutExtension` | Fallback title in table cell |
| `Format_NullFields_RenderAsEmptyCell` | Null values produce empty cells |
| `Format_PipeInCellValue_IsEscaped` | Pipe `\|` characters are escaped |
| `Format_LongDescription_TruncatedAt100Chars` | Descriptions over 100 chars truncated |

### `JsonFormatterTests`

| Test | Description |
|------|-------------|
| `Format_ProducesValidJson` | Output is parseable JSON |
| `Format_ContainsExpectedFields` | Root has `scannedDirectory`, `scannedAt`, `books` |
| `Format_BookFields_UseCamelCase` | Properties follow camelCase naming |
| `Format_NullFields_AreOmitted` | Null metadata fields are absent from output |
| `Format_EmptyResult_HasEmptyBooksArray` | Empty `books` array for no results |

### `MobiMetadataExtractorTests`

| Test | Description |
|------|-------------|
| `Accepts_ValidExtensions_*` | `.mobi`, `.azw`, `.azw3`, `.prc`, `.MOBI` accepted |
| `Accepts_InvalidExtensions_*` | `.pdf`, `.epub`, `.txt` rejected |
| `ExtractAsync_ValidMobiFile_ExtractsTitle` | Reads EXTH record 503 (Updated Title) |
| `ExtractAsync_ValidMobiFile_ExtractsAuthor` | Reads EXTH record 100 (Author) |
| `ExtractAsync_ValidMobiFile_HasCorrectFormat` | Format string is `"MOBI"` |
| `ExtractAsync_ValidMobiFile_HasCorrectFileName` | FileName matches the input file |
| `ExtractAsync_TruncatedFile_FallsBackGracefully` | Short/corrupt file doesn't throw |

### `ChmMetadataExtractorTests`

| Test | Description |
|------|-------------|
| `Accepts_ChmExtension_ReturnsTrue` | `.chm` extension is accepted |
| `Accepts_ChmExtensionCaseInsensitive_*` | `.CHM`, `.Chm` variants accepted |
| `Accepts_InvalidExtensions_*` | `.pdf`, `.epub`, `.mobi`, `.txt` rejected |
| `ExtractAsync_ValidChmFile_ExtractsTitle` | Reads `#SYSTEM` tag code 3 (Title) |
| `ExtractAsync_ValidChmFile_HasCorrectFormat` | Format string is `"CHM"` |
| `ExtractAsync_ValidChmFile_HasCorrectFileName` | FileName matches the input file |
| `ExtractAsync_ValidChmFileWithLanguage_ExtractsLanguage` | LCID 1033 → `"en-US"` |
| `ExtractAsync_ValidChmFileWithSystemTimestamp_ExtractsModifiedDate` | `#SYSTEM` compile time populates `ModifiedDate` |
| `ExtractAsync_ValidChmFileWithWindowsStrings_UsesIndexFileForTags` | `#WINDOWS` / `#STRINGS` discover HHK tags |
| `ExtractAsync_ValidChmFileWithHtmlMetadata_ExtractsEnhancedMetadata` | HTML heuristics populate title/authors/publisher/ISBN/date/tags |
| `ExtractAsync_TruncatedFile_FallsBackGracefully` | Short file doesn't throw |
| `ExtractAsync_InvalidMagic_ReturnsNullTitle` | Non-CHM file returns null title |

### `EbookScannerServiceTests`

| Test | Description |
|------|-------------|
| `ScanAsync_EmptyDirectory_ReturnsEmptyCatalog` | Empty result for empty directory |
| `ScanAsync_NonRecursive_DoesNotScanSubdirectories` | Top-level only by default |
| `ScanAsync_Recursive_FindsFilesInSubdirectories` | `--recursive` finds nested files |
| `ScanAsync_FormatFilter_OnlyReturnsPdfFiles` | `Formats: [Pdf]` excludes other types |
| `ScanAsync_CorruptFile_IsSkippedGracefully` | Bad files invoke `onError`, not exceptions |
| `ExtractAsync_UnknownExtension_ReturnsNull` | Unsupported extension returns `null` |
| `ScanAsync_SetsScannedDirectory_ToFullPath` | Result path is fully qualified |
| `ScanAsync_NullProgress_DoesNotThrow` | Null progress reporter is safe |
| `ScanAsync_Progress_*` | Progress reporting: file path, current/total, timing |

## Dependencies

```xml
<PackageReference Include="Microsoft.NET.Test.Sdk" />
<PackageReference Include="xunit.v3" />
<PackageReference Include="xunit.runner.visualstudio" />
<PackageReference Include="coverlet.collector" />
```

> The test project targets `net10.0` and does not participate in AOT publishing. It tests the trimming-safe source paths in `EbookScanner.Core` via regular JIT execution.
