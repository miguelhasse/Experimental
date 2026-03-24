using EbookScanner.Core.Extractors;

namespace EbookScanner.Tests.Extractors;

/// <summary>
/// Tests the ChmMetadataExtractor using synthetically constructed CHM binary files.
/// The minimal CHM layout used here is:
///   [0..95]    ITSF header v3
///   [96..179]  ITSP header (84 bytes, dir section)
///   [180..691] PMGL chunk (512 bytes, one directory chunk)
///   [692..]    /#SYSTEM data
/// </summary>
public sealed class ChmMetadataExtractorTests
{
    private readonly ChmMetadataExtractor _extractor = new();

    [Fact]
    public void Accepts_ChmExtension_ReturnsTrue()
    {
        Assert.True(_extractor.Accepts("/path/to/help.chm"));
    }

    [Theory]
    [InlineData(".CHM")]
    [InlineData(".Chm")]
    public void Accepts_ChmExtensionCaseInsensitive_ReturnsTrue(string extension)
    {
        Assert.True(_extractor.Accepts($"/path/to/book{extension}"));
    }

    [Theory]
    [InlineData(".pdf")]
    [InlineData(".epub")]
    [InlineData(".mobi")]
    [InlineData(".txt")]
    public void Accepts_InvalidExtensions_ReturnsFalse(string extension)
    {
        Assert.False(_extractor.Accepts($"/path/to/book{extension}"));
    }

    [Fact]
    public async Task ExtractAsync_ValidChmFile_ExtractsTitle()
    {
        var filePath = CreateMinimalChmFile("My CHM Book");
        try
        {
            var metadata = await _extractor.ExtractAsync(filePath, TestContext.Current.CancellationToken);

            Assert.Equal("My CHM Book", metadata.Title);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExtractAsync_ValidChmFile_HasCorrectFormat()
    {
        var filePath = CreateMinimalChmFile("Title");
        try
        {
            var metadata = await _extractor.ExtractAsync(filePath, TestContext.Current.CancellationToken);

            Assert.Equal("CHM", metadata.Format);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExtractAsync_ValidChmFile_HasCorrectFileName()
    {
        var filePath = CreateMinimalChmFile("Title");
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
    public async Task ExtractAsync_ValidChmFileWithLanguage_ExtractsLanguage()
    {
        const uint enUsLcid = 1033; // en-US
        var filePath = CreateMinimalChmFile("Title", lcid: enUsLcid);
        try
        {
            var metadata = await _extractor.ExtractAsync(filePath, TestContext.Current.CancellationToken);

            Assert.Equal("en-US", metadata.Language);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExtractAsync_TruncatedFile_FallsBackGracefully()
    {
        var filePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".chm");
        await File.WriteAllBytesAsync(filePath, new byte[32], TestContext.Current.CancellationToken);
        try
        {
            var metadata = await _extractor.ExtractAsync(filePath, TestContext.Current.CancellationToken);

            Assert.Equal("CHM", metadata.Format);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExtractAsync_InvalidMagic_ReturnsNullTitle()
    {
        var filePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".chm");
        var garbage = new byte[256];
        "NOTCHM"u8.CopyTo(garbage);
        await File.WriteAllBytesAsync(filePath, garbage, TestContext.Current.CancellationToken);
        try
        {
            var metadata = await _extractor.ExtractAsync(filePath, TestContext.Current.CancellationToken);

            Assert.Equal("CHM", metadata.Format);
            Assert.Null(metadata.Title);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    /// <summary>
    /// Builds a minimal but structurally valid CHM file in memory containing a
    /// /#SYSTEM block with the given title (and optionally a language LCID).
    /// </summary>
    private static string CreateMinimalChmFile(string title, uint lcid = 0)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".chm");
        using var ms = new MemoryStream();

        // ── Layout constants ────────────────────────────────────────────────────
        const uint dirOffset  = 96;   // ITSF header is exactly 96 bytes (v3)
        const uint itspHdrLen = 84;   // ITSP header is exactly 84 bytes
        const uint blockLen   = 512;  // PMGL chunk size
        const uint dataOffset = dirOffset + itspHdrLen + blockLen; // = 692

        var systemData = BuildSystemData(title, lcid);

        WriteItsfHeader(ms, dirOffset, itspHdrLen + blockLen, dataOffset, lcid);
        WriteItspHeader(ms, itspHdrLen, blockLen);
        WritePmglChunk(ms, blockLen, systemData.Length);
        ms.Write(systemData);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    // ── Binary structure builders ────────────────────────────────────────────────

    private static void WriteItsfHeader(
        Stream ms, uint dirOffset, uint dirLen, uint dataOffset, uint langId)
    {
        ms.Write("ITSF"u8);                        // magic
        WriteLE32(ms, 3);                           // version 3
        WriteLE32(ms, 96);                          // header length
        WriteLE32(ms, 1);                           // unknown
        WriteLE32(ms, 0);                           // timestamp
        WriteLE32(ms, langId);                      // language ID
        ms.Write(new byte[16]);                     // GUID1
        ms.Write(new byte[16]);                     // GUID2
        WriteLE64(ms, 0);                           // unknown offset (section 0)
        WriteLE64(ms, 0);                           // unknown length (section 0)
        WriteLE64(ms, dirOffset);                   // dir section offset  (0x48)
        WriteLE64(ms, dirLen);                      // dir section length  (0x50)
        WriteLE64(ms, dataOffset);                  // data offset         (0x58, v3)
    }

    private static void WriteItspHeader(Stream ms, uint itspHdrLen, uint blockLen)
    {
        ms.Write("ITSP"u8);                        // magic
        WriteLE32(ms, 1);                           // version
        WriteLE32(ms, itspHdrLen);                  // header length (84)
        WriteLE32(ms, 0);                           // unknown
        WriteLE32(ms, blockLen);                    // chunk size
        WriteLE32(ms, 2);                           // quickref density
        WriteLE32(ms, 1);                           // index depth (1 = flat, no index)
        WriteLE32(ms, unchecked((uint)-1));         // index_root  = -1 (no index chunk)
        WriteLE32(ms, 0);                           // index_head  = chunk 0 (first PMGL)
        WriteLE32(ms, 0);                           // index_tail  = chunk 0 (only one chunk)
        WriteLE32(ms, unchecked((uint)-1));         // unknown = -1
        WriteLE32(ms, 1);                           // num_blocks
        WriteLE32(ms, 0);                           // lang_id
        ms.Write(new byte[16]);                     // GUID
        ms.Write(new byte[16]);                     // unknown_0044
    }

    private static void WritePmglChunk(Stream ms, uint blockLen, int systemDataLen)
    {
        // ── Build the /#SYSTEM entry ─────────────────────────────────────────────
        using var entryMs = new MemoryStream();
        WriteEncInt(entryMs, 8);                    // name length = len("/#SYSTEM")
        entryMs.Write("/#SYSTEM"u8);
        WriteEncInt(entryMs, 0);                    // content section = 0 (uncompressed)
        WriteEncInt(entryMs, 0);                    // file offset = 0
        WriteEncInt(entryMs, systemDataLen);        // file length

        int entrySize  = (int)entryMs.Length;
        uint freeSpace = blockLen - 20u - (uint)entrySize;

        // ── PMGL header (20 bytes) ───────────────────────────────────────────────
        ms.Write("PMGL"u8);
        WriteLE32(ms, freeSpace);                   // free space at end of chunk
        WriteLE32(ms, 0);                           // unknown
        WriteLE32(ms, unchecked((uint)-1));         // block_prev = -1
        WriteLE32(ms, unchecked((uint)-1));         // block_next = -1

        // ── Entry ───────────────────────────────────────────────────────────────
        entryMs.Position = 0;
        entryMs.CopyTo(ms);

        // ── Padding to fill the chunk ────────────────────────────────────────────
        ms.Write(new byte[freeSpace]);
    }

    private static byte[] BuildSystemData(string title, uint lcid)
    {
        using var ms = new MemoryStream();

        WriteLE32(ms, 3);                           // version = 3

        // Code 3: Title (null-terminated UTF-8 string)
        var titleBytes = System.Text.Encoding.UTF8.GetBytes(title + "\0");
        WriteLE16(ms, 3);
        WriteLE16(ms, (ushort)titleBytes.Length);
        ms.Write(titleBytes);

        // Code 10: LCID (4-byte language ID)
        if (lcid != 0)
        {
            WriteLE16(ms, 10);
            WriteLE16(ms, 4);
            WriteLE32(ms, lcid);
        }

        return ms.ToArray();
    }

    // ── Encoding helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a CHM variable-length encoded integer (big-endian, 7 bits per byte).
    /// </summary>
    private static void WriteEncInt(Stream ms, int value)
    {
        if (value < 0x80)
        {
            ms.WriteByte((byte)value);
            return;
        }
        // Two bytes: first byte has high bit set, second does not.
        ms.WriteByte((byte)((value >> 7) | 0x80));
        ms.WriteByte((byte)(value & 0x7F));
    }

    private static void WriteLE16(Stream ms, ushort v)
    {
        ms.WriteByte((byte)v);
        ms.WriteByte((byte)(v >> 8));
    }

    private static void WriteLE32(Stream ms, uint v)
    {
        ms.WriteByte((byte)v);
        ms.WriteByte((byte)(v >> 8));
        ms.WriteByte((byte)(v >> 16));
        ms.WriteByte((byte)(v >> 24));
    }

    private static void WriteLE64(Stream ms, ulong v)
    {
        WriteLE32(ms, (uint)v);
        WriteLE32(ms, (uint)(v >> 32));
    }
}
