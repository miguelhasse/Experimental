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

## Test coverage

| Test | Description |
|---|---|
| `PlainText_ReturnsContent` | `PlainTextConverter` extracts text from a `text/plain` stream |
| `HTML_ReturnsMarkdown` | `HtmlConverter` converts `<h1>` and `<p>` to Markdown heading and paragraph |
| `CSV_ReturnsPipeTable` | `CsvConverter` produces a Markdown pipe table with header separator row |
| `Json_Extension_AcceptedAsPlainText` | `.json` extension is handled by `PlainTextConverter` (not rejected) |
| `UnsupportedFormat_Throws` | Converting a stream with an unknown MIME and no extension throws `UnsupportedFormatException` |
| `EmptyHTML_ReturnsEmpty` | An HTML document with no body text returns an empty (or whitespace-only) Markdown string |
| `CSV_PipeCharacterEscaped` | Pipe characters `|` inside CSV cells are escaped to `\|` in the table output |
| `DataUri_RoundTrip` | A `data:text/plain;base64,...` URI is decoded and converted correctly |
| `StreamInfo_Merge_OverlayWins` | `StreamInfo.Merge()` — non-null properties in the overlay replace those in the base |
| `StreamInfo_Merge_BaseKeptWhenOverlayNull` | `StreamInfo.Merge()` — null properties in the overlay do not overwrite base values |
| `ConverterPriority_Sorted` | `MarkItDownService.Converters` is returned in ascending priority order |
| `Service_IsDisposable` | `MarkItDownService` implements `IDisposable`; `Dispose()` does not throw |
| `BinaryMime_RejectedByPlainText` | `PlainTextConverter` does not accept `application/pdf` streams (binary MIME guard) |

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
