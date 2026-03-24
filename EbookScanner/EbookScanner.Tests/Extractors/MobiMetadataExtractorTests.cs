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
            var metadata = await _extractor.ExtractAsync(filePath);

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
            var metadata = await _extractor.ExtractAsync(filePath);

            Assert.NotNull(metadata.Authors);
            Assert.Contains("John Smith", metadata.Authors);
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
            var metadata = await _extractor.ExtractAsync(filePath);

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
            var metadata = await _extractor.ExtractAsync(filePath);

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
        await File.WriteAllBytesAsync(filePath, bytes);
        try
        {
            var metadata = await _extractor.ExtractAsync(filePath);
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
    private static string CreateMinimalMobiFile(string title, string author)
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
        // PalmDOC header (32 bytes of zeros)
        ms.Write(new byte[32]);

        // MOBI header starts here (offset 32 within record 0)
        // Magic "MOBI"
        ms.Write("MOBI"u8);
        // MOBI header length (offset 36 within record 0, 4 bytes)
        uint mobiHeaderLength = 248; // standard MOBI header length
        WriteUInt32BE(ms, mobiHeaderLength);
        // Fill the rest of the MOBI header with zeros up to offset 84 (full title offset)
        ms.Write(new byte[84 - 8]); // 76 zero bytes (we wrote 4+4=8 so far)

        // Full title offset (offset 84): place it after MOBI header end = 32 + mobiHeaderLength
        uint fullTitleOffset = 32 + mobiHeaderLength + (uint)ExthBlockSize(title, author);
        WriteUInt32BE(ms, fullTitleOffset); // offset 84
        WriteUInt32BE(ms, (uint)System.Text.Encoding.UTF8.GetByteCount(title)); // offset 88
        // Fill remaining MOBI header bytes to reach mobiHeaderLength
        int mobiWritten = 4 + 4 + (84 - 8) + 4 + 4; // bytes written so far in MOBI header
        ms.Write(new byte[mobiHeaderLength - mobiWritten]);

        // EXTH header immediately after MOBI header
        WriteExthBlock(ms, title, author);

        // Full title (at the offset we calculated)
        ms.Write(System.Text.Encoding.UTF8.GetBytes(title));

        ms.Position = 0;
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static int ExthBlockSize(string title, string author)
    {
        var titleBytes = System.Text.Encoding.UTF8.GetByteCount(title);
        var authorBytes = System.Text.Encoding.UTF8.GetByteCount(author);
        // EXTH header (12) + record 503 (8 + titleBytes) + record 100 (8 + authorBytes)
        return 12 + (8 + titleBytes) + (8 + authorBytes);
    }

    private static void WriteExthBlock(Stream stream, string title, string author)
    {
        var titleBytes = System.Text.Encoding.UTF8.GetBytes(title);
        var authorBytes = System.Text.Encoding.UTF8.GetBytes(author);

        // EXTH magic
        stream.Write("EXTH"u8);
        // EXTH header length (12 + records)
        uint exthLen = (uint)ExthBlockSize(title, author);
        WriteUInt32BE(stream, exthLen);
        // Record count = 2
        WriteUInt32BE(stream, 2);

        // Record 503 (Updated Title)
        WriteUInt32BE(stream, 503);
        WriteUInt32BE(stream, (uint)(8 + titleBytes.Length));
        stream.Write(titleBytes);

        // Record 100 (Author)
        WriteUInt32BE(stream, 100);
        WriteUInt32BE(stream, (uint)(8 + authorBytes.Length));
        stream.Write(authorBytes);
    }

    private static void WriteUInt32BE(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}
