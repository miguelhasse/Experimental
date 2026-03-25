using EbookScanner.Core.Extractors;

namespace EbookScanner.Tests.Extractors;

/// <summary>
/// Tests the MobiMetadataExtractor using synthetically constructed MOBI binary files.
/// </summary>
public sealed class MobiMetadataExtractorTests
{
    private readonly MobiMetadataExtractor _extractor = new();

    [Theory]
    [InlineData(".mobi")]
    [InlineData(".azw")]
    [InlineData(".azw3")]
    [InlineData(".prc")]
    [InlineData(".MOBI")]
    public void Accepts_ValidExtensions_ReturnsTrue(string extension)
    {
        Assert.True(_extractor.Accepts($"/path/to/book{extension}"));
    }

    [Theory]
    [InlineData(".pdf")]
    [InlineData(".epub")]
    [InlineData(".txt")]
    public void Accepts_InvalidExtensions_ReturnsFalse(string extension)
    {
        Assert.False(_extractor.Accepts($"/path/to/book{extension}"));
    }

    [Fact]
    public async Task ExtractAsync_ValidMobiFile_ExtractsTitle()
    {
        var filePath = CreateMinimalMobiFile("My MOBI Book", "Jane Doe");
        try
        {
            var metadata = await _extractor.ExtractAsync(filePath, TestContext.Current.CancellationToken);

            Assert.Equal("My MOBI Book", metadata.Title);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExtractAsync_ValidMobiFile_ExtractsAuthor()
    {
        var filePath = CreateMinimalMobiFile("Book Title", "John Smith");
        try
        {
            var metadata = await _extractor.ExtractAsync(filePath, TestContext.Current.CancellationToken);

            Assert.NotNull(metadata.Authors);
            Assert.Contains("John Smith", metadata.Authors);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExtractAsync_RealWorldHeaderOffset_ExtractsRichMetadata()
    {
        var filePath = CreateMinimalMobiFile(
            "Azure Data Factory by Example",
            "Richard Swinbank",
            palmDocHeaderLength: 16,
            publisher: "Apress",
            description: "A practical guide to data pipelines.",
            isbn: "9781484299999",
            publishedDate: "2024-03-24",
            language: "en",
            tags:
            [
                "Data Engineering",
                "Azure"
            ]);

        try
        {
            var metadata = await _extractor.ExtractAsync(filePath, TestContext.Current.CancellationToken);

            Assert.Equal("Azure Data Factory by Example", metadata.Title);
            Assert.NotNull(metadata.Authors);
            Assert.Equal(["Richard Swinbank"], metadata.Authors);
            Assert.Equal("Apress", metadata.Publisher);
            Assert.Equal("A practical guide to data pipelines.", metadata.Description);
            Assert.Equal("9781484299999", metadata.Isbn);
            Assert.Equal(new DateTimeOffset(2024, 03, 24, 0, 0, 0, TimeSpan.Zero), metadata.PublishedDate);
            Assert.Equal("en", metadata.Language);
            Assert.NotNull(metadata.Tags);
            Assert.Equal(["Data Engineering", "Azure"], metadata.Tags);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExtractAsync_LegacySyntheticHeaderOffset_StillExtractsMetadata()
    {
        var filePath = CreateMinimalMobiFile("Legacy Layout Title", "Legacy Author", palmDocHeaderLength: 32);
        try
        {
            var metadata = await _extractor.ExtractAsync(filePath, TestContext.Current.CancellationToken);

            Assert.Equal("Legacy Layout Title", metadata.Title);
            Assert.NotNull(metadata.Authors);
            Assert.Equal(["Legacy Author"], metadata.Authors);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExtractAsync_ValidMobiFile_HasCorrectFormat()
    {
        var filePath = CreateMinimalMobiFile("Title", "Author");
        try
        {
            var metadata = await _extractor.ExtractAsync(filePath, TestContext.Current.CancellationToken);

            Assert.Equal("MOBI", metadata.Format);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExtractAsync_ValidMobiFile_HasCorrectFileName()
    {
        var filePath = CreateMinimalMobiFile("Title", "Author");
        try
        {
            var metadata = await _extractor.ExtractAsync(filePath, TestContext.Current.CancellationToken);

            Assert.Equal(Path.GetFileName(filePath), metadata.FileName);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExtractAsync_TruncatedFile_FallsBackGracefully()
    {
        var filePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mobi");
        // Write a PalmDB name then truncate — should not throw
        var bytes = new byte[32];
        "Fallback Title\0"u8.TryCopyTo(bytes);
        await File.WriteAllBytesAsync(filePath, bytes, TestContext.Current.CancellationToken);
        try
        {
            var metadata = await _extractor.ExtractAsync(filePath, TestContext.Current.CancellationToken);
            Assert.Equal("MOBI", metadata.Format);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    /// <summary>
    /// Builds a minimal but structurally valid MOBI file in memory with an EXTH block
    /// containing the given title (record 503) and author (record 100).
    /// </summary>
    private static string CreateMinimalMobiFile(
        string title,
        string author,
        int palmDocHeaderLength = 16,
        string? publisher = null,
        string? description = null,
        string? isbn = null,
        string? publishedDate = null,
        string? language = null,
        IReadOnlyList<string>? tags = null)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mobi");
        using var ms = new MemoryStream();

        // ── PalmDB Header ──────────────────────────────────────────────────────
        // Bytes 0-31: database name (pad to 32)
        var nameBytes = new byte[32];
        var palmName = System.Text.Encoding.Latin1.GetBytes("TestBook");
        palmName.CopyTo(nameBytes, 0);
        ms.Write(nameBytes);

        // Bytes 32-75: Palm DB fields (skip; 44 bytes of zeros)
        ms.Write(new byte[44]);

        // Bytes 76-77: record count = 1 (big-endian)
        ms.WriteByte(0); ms.WriteByte(1);

        // Record list entry for record 0 (8 bytes): offset = 86 (header size), uid = 0
        // Header size = 78 (PalmDB header) + 8 (one record entry) = 86
        uint record0Offset = 86;
        WriteUInt32BE(ms, record0Offset);
        WriteUInt32BE(ms, 0); // attributes + unique id

        // ── Record 0 ─────────────────────────────────────────────────────────
        // PalmDOC header bytes before the MOBI header. Real files commonly use 16 bytes.
        ms.Write(new byte[palmDocHeaderLength]);

        // MOBI header starts after the PalmDOC header.
        // Magic "MOBI"
        ms.Write("MOBI"u8);
        // MOBI header length (4 bytes immediately after the magic)
        uint mobiHeaderLength = 248; // standard MOBI header length
        WriteUInt32BE(ms, mobiHeaderLength);
        // Fill the rest of the MOBI header with zeros up to offset 84 relative to the record start.
        int bytesWrittenInRecord = palmDocHeaderLength + 8;
        if (bytesWrittenInRecord > 84)
            throw new InvalidOperationException("PalmDOC header is too large for the synthetic fixture.");

        ms.Write(new byte[84 - bytesWrittenInRecord]);

        // Full title offset/length at record offsets 84/88 for real-world layout compatibility.
        uint fullTitleOffset = (uint)(palmDocHeaderLength + mobiHeaderLength + ExthBlockSize(title, author, publisher, description, isbn, publishedDate, language, tags));
        WriteUInt32BE(ms, fullTitleOffset);
        WriteUInt32BE(ms, (uint)System.Text.Encoding.UTF8.GetByteCount(title));

        int bytesWrittenInMobiHeader = (int)ms.Position - (int)record0Offset - palmDocHeaderLength;
        ms.Write(new byte[mobiHeaderLength - bytesWrittenInMobiHeader]);

        // EXTH header immediately after MOBI header
        WriteExthBlock(ms, title, author, publisher, description, isbn, publishedDate, language, tags);

        // Full title (at the offset we calculated)
        ms.Write(System.Text.Encoding.UTF8.GetBytes(title));

        ms.Position = 0;
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static int ExthBlockSize(
        string title,
        string author,
        string? publisher,
        string? description,
        string? isbn,
        string? publishedDate,
        string? language,
        IReadOnlyList<string>? tags)
    {
        var titleBytes = System.Text.Encoding.UTF8.GetByteCount(title);
        var authorBytes = System.Text.Encoding.UTF8.GetByteCount(author);
        int size = 12 + (8 + titleBytes) + (8 + authorBytes);
        size += ExthRecordSize(publisher);
        size += ExthRecordSize(description);
        size += ExthRecordSize(isbn);
        size += ExthRecordSize(publishedDate);
        size += ExthRecordSize(language);
        if (tags is not null)
        {
            foreach (var tag in tags)
                size += ExthRecordSize(tag);
        }

        return size;
    }

    private static int ExthRecordSize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? 0 : 8 + System.Text.Encoding.UTF8.GetByteCount(value);

    private static void WriteExthBlock(
        Stream stream,
        string title,
        string author,
        string? publisher,
        string? description,
        string? isbn,
        string? publishedDate,
        string? language,
        IReadOnlyList<string>? tags)
    {
        var titleBytes = System.Text.Encoding.UTF8.GetBytes(title);
        var authorBytes = System.Text.Encoding.UTF8.GetBytes(author);
        var records = new List<(uint Type, byte[] Data)>
        {
            (503, titleBytes),
            (100, authorBytes)
        };

        AddOptionalRecord(records, 101, publisher);
        AddOptionalRecord(records, 103, description);
        AddOptionalRecord(records, 104, isbn);
        AddOptionalRecord(records, 106, publishedDate);
        AddOptionalRecord(records, 524, language);
        if (tags is not null)
        {
            foreach (var tag in tags)
                AddOptionalRecord(records, 105, tag);
        }

        // EXTH magic
        stream.Write("EXTH"u8);
        // EXTH header length (12 + records)
        uint exthLen = (uint)(12 + records.Sum(record => 8 + record.Data.Length));
        WriteUInt32BE(stream, exthLen);
        WriteUInt32BE(stream, (uint)records.Count);

        foreach (var record in records)
        {
            WriteUInt32BE(stream, record.Type);
            WriteUInt32BE(stream, (uint)(8 + record.Data.Length));
            stream.Write(record.Data);
        }
    }

    private static void AddOptionalRecord(List<(uint Type, byte[] Data)> records, uint type, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        records.Add((type, System.Text.Encoding.UTF8.GetBytes(value)));
    }

    private static void WriteUInt32BE(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}
