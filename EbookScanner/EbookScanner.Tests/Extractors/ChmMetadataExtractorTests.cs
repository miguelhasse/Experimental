using System.Text;
using EbookScanner.Core.Extractors;

namespace EbookScanner.Tests.Extractors;

/// <summary>
/// Tests the ChmMetadataExtractor using synthetically constructed CHM binary files.
/// The builder in this file produces a minimal v3 CHM with one PMGL directory chunk
/// and any number of section-0 (uncompressed) objects.
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
        var filePath = CreateChmFile(new ChmFixtureOptions(
            Title: "My CHM Book"));

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
        var filePath = CreateChmFile(new ChmFixtureOptions(Title: "Title"));
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
        var filePath = CreateChmFile(new ChmFixtureOptions(Title: "Title"));
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
        const uint enUsLcid = 1033;
        var filePath = CreateChmFile(new ChmFixtureOptions(
            Title: "Title",
            HeaderLcid: enUsLcid,
            SystemLcid: enUsLcid));

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
    public async Task ExtractAsync_ValidChmFileWithSystemTimestamp_ExtractsModifiedDate()
    {
        var timestamp = new DateTimeOffset(2024, 03, 05, 12, 30, 45, TimeSpan.Zero);
        var filePath = CreateChmFile(new ChmFixtureOptions(
            Title: "Title",
            SystemFileTimeUtc: timestamp));

        try
        {
            var metadata = await _extractor.ExtractAsync(filePath, TestContext.Current.CancellationToken);

            Assert.Equal(timestamp, metadata.ModifiedDate);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExtractAsync_ValidChmFileWithWindowsStrings_UsesIndexFileForTags()
    {
        const string hhk = """
<!DOCTYPE HTML PUBLIC "-//IETF//DTD HTML//EN">
<HTML><BODY>
<UL>
  <LI><OBJECT type="text/sitemap">
      <param name="Name" value="Programming">
      <param name="Local" value="index.html">
  </OBJECT>
  <LI><OBJECT type="text/sitemap">
      <param name="Name" value="CSharp">
      <param name="Local" value="index.html">
  </OBJECT>
</UL>
</BODY></HTML>
""";

        var filePath = CreateChmFile(new ChmFixtureOptions(
            Title: "Title",
            WindowTitle: "Window Title",
            DefaultTopic: "/index.html",
            IndexFile: "/book.hhk",
            AdditionalFiles:
            [
                new ChmFixtureFile("/book.hhk", Encoding.UTF8.GetBytes(hhk))
            ]));

        try
        {
            var metadata = await _extractor.ExtractAsync(filePath, TestContext.Current.CancellationToken);

            Assert.NotNull(metadata.Tags);
            Assert.Equal(["Programming", "CSharp"], metadata.Tags);
            Assert.Equal("Title", metadata.Title);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExtractAsync_ValidChmFileWithHtmlMetadata_ExtractsEnhancedMetadata()
    {
        const string html = """
<!doctype html>
<html>
<head>
  <title>Actual HTML Title</title>
  <meta name="author" content="Ada Lovelace; Grace Hopper" />
  <meta name="publisher" content="Tech Books Press" />
  <meta name="description" content="A practical guide to CHM metadata." />
  <meta name="keywords" content="metadata, chm, parsing" />
  <meta name="dc.date" content="2021-06-15" />
</head>
<body>
  <p>ISBN: 978-1-4028-9462-6</p>
</body>
</html>
""";

        var filePath = CreateChmFile(new ChmFixtureOptions(
            Title: null,
            DefaultTopic: "/index.html",
            AdditionalFiles:
            [
                new ChmFixtureFile("/index.html", Encoding.UTF8.GetBytes(html))
            ]));

        try
        {
            var metadata = await _extractor.ExtractAsync(filePath, TestContext.Current.CancellationToken);

            Assert.Equal("Actual HTML Title", metadata.Title);
            Assert.NotNull(metadata.Authors);
            Assert.Equal(["Ada Lovelace", "Grace Hopper"], metadata.Authors);
            Assert.Equal("Tech Books Press", metadata.Publisher);
            Assert.Equal("A practical guide to CHM metadata.", metadata.Description);
            Assert.Equal("9781402894626", metadata.Isbn);
            Assert.Equal(new DateTimeOffset(2021, 06, 15, 0, 0, 0, TimeSpan.Zero), metadata.PublishedDate);
            Assert.NotNull(metadata.Tags);
            Assert.Equal(["metadata", "chm", "parsing"], metadata.Tags);
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

    private static string CreateChmFile(ChmFixtureOptions options)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".chm");
        using var ms = new MemoryStream();

        const uint dirOffset = 96;
        const uint itspHeaderLength = 84;
        const uint blockLength = 2048;
        const uint dataOffset = dirOffset + itspHeaderLength + blockLength;

        var files = new List<ChmFixtureFile>();
        var systemData = BuildSystemData(options);
        files.Add(new ChmFixtureFile("/#SYSTEM", systemData));

        if (!string.IsNullOrWhiteSpace(options.WindowTitle) ||
            !string.IsNullOrWhiteSpace(options.DefaultTopic) ||
            !string.IsNullOrWhiteSpace(options.IndexFile))
        {
            var stringsData = BuildStringsData(options, out var titleOffset, out var defaultOffset, out var indexOffset);
            var windowsData = BuildWindowsData(titleOffset, defaultOffset, indexOffset);
            files.Add(new ChmFixtureFile("/#WINDOWS", windowsData));
            files.Add(new ChmFixtureFile("/#STRINGS", stringsData));
        }

        if (options.AdditionalFiles is not null)
            files.AddRange(options.AdditionalFiles);

        WriteItsfHeader(ms, dirOffset, itspHeaderLength + blockLength, dataOffset, options.HeaderLcid);
        WriteItspHeader(ms, itspHeaderLength, blockLength);
        WritePmglChunk(ms, blockLength, files);
        WriteSectionZeroData(ms, files);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static void WriteSectionZeroData(Stream stream, IReadOnlyList<ChmFixtureFile> files)
    {
        foreach (var file in files)
        {
            stream.Write(file.Data);
        }
    }

    private static byte[] BuildSystemData(ChmFixtureOptions options)
    {
        using var ms = new MemoryStream();
        WriteLE32(ms, 3);

        if (!string.IsNullOrWhiteSpace(options.Title))
        {
            var titleBytes = Encoding.UTF8.GetBytes(options.Title + "\0");
            WriteLE16(ms, 3);
            WriteLE16(ms, (ushort)titleBytes.Length);
            ms.Write(titleBytes);
        }

        if (options.SystemLcid != 0 || options.SystemFileTimeUtc is not null)
        {
            Span<byte> payload = stackalloc byte[28];
            if (options.SystemLcid != 0)
                WriteLE32(payload, 0, options.SystemLcid);
            if (options.SystemFileTimeUtc is not null)
                WriteLE64(payload, 20, (ulong)options.SystemFileTimeUtc.Value.ToFileTime());

            WriteLE16(ms, 4);
            WriteLE16(ms, 28);
            ms.Write(payload);
        }

        if (options.LegacyCode10Lcid != 0)
        {
            WriteLE16(ms, 10);
            WriteLE16(ms, 4);
            WriteLE32(ms, options.LegacyCode10Lcid);
        }

        if (!string.IsNullOrWhiteSpace(options.DefaultTopic))
        {
            var topicBytes = Encoding.UTF8.GetBytes(options.DefaultTopic.TrimStart('/') + "\0");
            WriteLE16(ms, 2);
            WriteLE16(ms, (ushort)topicBytes.Length);
            ms.Write(topicBytes);
        }

        if (!string.IsNullOrWhiteSpace(options.IndexFile))
        {
            var indexBytes = Encoding.UTF8.GetBytes(options.IndexFile.TrimStart('/') + "\0");
            WriteLE16(ms, 1);
            WriteLE16(ms, (ushort)indexBytes.Length);
            ms.Write(indexBytes);
        }

        return ms.ToArray();
    }

    private static byte[] BuildStringsData(
        ChmFixtureOptions options,
        out uint titleOffset,
        out uint defaultOffset,
        out uint indexOffset)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0);

        titleOffset = WriteCString(ms, options.WindowTitle);
        defaultOffset = WriteCString(ms, options.DefaultTopic?.TrimStart('/'));
        indexOffset = WriteCString(ms, options.IndexFile?.TrimStart('/'));

        return ms.ToArray();
    }

    private static byte[] BuildWindowsData(uint titleOffset, uint defaultOffset, uint indexOffset)
    {
        const uint entrySize = 0x6C;
        using var ms = new MemoryStream();
        WriteLE32(ms, 1);
        WriteLE32(ms, entrySize);

        var entry = new byte[entrySize];
        WriteLE32(entry, 0x14, titleOffset);
        WriteLE32(entry, 0x64, indexOffset);
        WriteLE32(entry, 0x68, defaultOffset);
        ms.Write(entry);
        return ms.ToArray();
    }

    private static uint WriteCString(Stream stream, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        uint offset = (uint)stream.Position;
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        stream.Write(bytes);
        return offset;
    }

    private static void WritePmglChunk(Stream stream, uint blockLength, IReadOnlyList<ChmFixtureFile> files)
    {
        using var entriesStream = new MemoryStream();
        long currentOffset = 0;
        foreach (var file in files)
        {
            WriteEncInt(entriesStream, file.Path.Length);
            entriesStream.Write(Encoding.UTF8.GetBytes(file.Path));
            WriteEncInt(entriesStream, 0);
            WriteEncInt(entriesStream, currentOffset);
            WriteEncInt(entriesStream, file.Data.Length);
            currentOffset += file.Data.Length;
        }

        int entrySize = (int)entriesStream.Length;
        uint freeSpace = blockLength - 20u - (uint)entrySize;

        stream.Write("PMGL"u8);
        WriteLE32(stream, freeSpace);
        WriteLE32(stream, 0);
        WriteLE32(stream, unchecked((uint)-1));
        WriteLE32(stream, unchecked((uint)-1));

        entriesStream.Position = 0;
        entriesStream.CopyTo(stream);
        stream.Write(new byte[freeSpace]);
    }

    private static void WriteItsfHeader(Stream stream, uint dirOffset, uint dirLength, uint dataOffset, uint langId)
    {
        stream.Write("ITSF"u8);
        WriteLE32(stream, 3);
        WriteLE32(stream, 96);
        WriteLE32(stream, 1);
        WriteLE32(stream, 0);
        WriteLE32(stream, langId);
        stream.Write(new byte[16]);
        stream.Write(new byte[16]);
        WriteLE64(stream, 0);
        WriteLE64(stream, 0);
        WriteLE64(stream, dirOffset);
        WriteLE64(stream, dirLength);
        WriteLE64(stream, dataOffset);
    }

    private static void WriteItspHeader(Stream stream, uint headerLength, uint blockLength)
    {
        stream.Write("ITSP"u8);
        WriteLE32(stream, 1);
        WriteLE32(stream, headerLength);
        WriteLE32(stream, 0);
        WriteLE32(stream, blockLength);
        WriteLE32(stream, 2);
        WriteLE32(stream, 1);
        WriteLE32(stream, unchecked((uint)-1));
        WriteLE32(stream, 0);
        WriteLE32(stream, 0);
        WriteLE32(stream, unchecked((uint)-1));
        WriteLE32(stream, 1);
        WriteLE32(stream, 0);
        stream.Write(new byte[16]);
        stream.Write(new byte[16]);
    }

    private static void WriteEncInt(Stream stream, long value)
    {
        if (value < 0x80)
        {
            stream.WriteByte((byte)value);
            return;
        }

        var bytes = new List<byte>();
        long remaining = value;
        bytes.Add((byte)(remaining & 0x7F));
        remaining >>= 7;
        while (remaining > 0)
        {
            bytes.Add((byte)((remaining & 0x7F) | 0x80));
            remaining >>= 7;
        }

        for (int i = bytes.Count - 1; i >= 0; i--)
        {
            byte b = bytes[i];
            if (i == 0)
                b &= 0x7F;
            else
                b |= 0x80;

            stream.WriteByte(b);
        }
    }

    private static void WriteLE16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
    }

    private static void WriteLE32(Stream stream, uint value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 24));
    }

    private static void WriteLE64(Stream stream, ulong value)
    {
        WriteLE32(stream, (uint)value);
        WriteLE32(stream, (uint)(value >> 32));
    }

    private static void WriteLE32(Span<byte> buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteLE64(Span<byte> buffer, int offset, ulong value)
    {
        WriteLE32(buffer, offset, (uint)value);
        WriteLE32(buffer, offset + 4, (uint)(value >> 32));
    }

    private static void WriteLE32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private sealed record ChmFixtureOptions(
        string? Title = null,
        uint HeaderLcid = 0,
        uint SystemLcid = 0,
        DateTimeOffset? SystemFileTimeUtc = null,
        uint LegacyCode10Lcid = 0,
        string? WindowTitle = null,
        string? DefaultTopic = null,
        string? IndexFile = null,
        IReadOnlyList<ChmFixtureFile>? AdditionalFiles = null);

    private sealed record ChmFixtureFile(string Path, byte[] Data);
}
