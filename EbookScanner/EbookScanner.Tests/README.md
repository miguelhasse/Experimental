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
│   ├── MarkdownFormatterTests.cs    — catalog output, field formatting, edge cases
│   └── JsonFormatterTests.cs       — JSON structure, camelCase, null omission
├── Extractors/
│   └── MobiMetadataExtractorTests.cs — file extension acceptance, binary parsing
└── EbookScannerServiceTests.cs      — directory scanning, recursion, format filtering
```

## Test Coverage

### `MarkdownFormatterTests`

| Test | Description |
|------|-------------|
| `Format_EmptyResult_ContainsCatalogHeader` | Verifies header and directory are included |
| `Format_SingleBook_ContainsTitle` | Book title appears as a section heading |
| `Format_SingleBook_ContainsAllFields` | Authors, publisher, ISBN, pages, date, tags, size |
| `Format_MultipleBooks_ShowsFormatBreakdown` | PDF/EPUB/MOBI counts in summary line |
| `Format_BookWithNoTitle_UsesFileNameWithoutExtension` | Fallback title from filename |
| `Format_LongDescription_IsTruncated` | Descriptions over 200 chars are truncated |

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
| `ScanAsync_ScannedAt_IsRecentUtcTime` | Timestamp is within the test window |

## Dependencies

```xml
<PackageReference Include="Microsoft.NET.Test.Sdk" />
<PackageReference Include="xunit.v3" />
<PackageReference Include="xunit.runner.visualstudio" />
<PackageReference Include="coverlet.collector" />
```

> The test project targets `net10.0` and does not participate in AOT publishing. It tests the trimming-safe source paths in `EbookScanner.Core` via regular JIT execution.
