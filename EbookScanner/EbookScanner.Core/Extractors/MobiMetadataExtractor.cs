using System.Globalization;
using System.Text;
using EbookScanner.Core.Models;

namespace EbookScanner.Core.Extractors;

/// <summary>
/// Extracts metadata from MOBI/AZW files by parsing the Palm Database Format
/// header and the EXTH (Extended Header) metadata block.
/// </summary>
public sealed class MobiMetadataExtractor : BookMetadataExtractor
{
    private static readonly string[] MobiExtensions = [".mobi", ".azw", ".azw3", ".prc"];
    private const int MaxRecordZeroLength = 65_536;
    private static ReadOnlySpan<byte> MobiMagic => "MOBI"u8;
    private static ReadOnlySpan<byte> ExthMagic => "EXTH"u8;

    public override bool Accepts(string filePath) =>
        MobiExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase);

    public override Task<BookMetadata> ExtractAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(filePath);
        var meta = ParseMobiMetadata(filePath);

        return Task.FromResult(new BookMetadata(
            FilePath: filePath,
            FileName: fileInfo.Name,
            Format: "MOBI",
            FileSizeBytes: fileInfo.Length,
            Title: meta.Title,
            Authors: meta.Authors,
            Publisher: meta.Publisher,
            Description: meta.Description,
            Language: meta.Language,
            Isbn: meta.Isbn,
            PublishedDate: meta.PublishedDate,
            ModifiedDate: null,
            PageCount: null,
            Tags: meta.Tags));
    }

    private static MobiRawMetadata ParseMobiMetadata(string filePath)
    {
        // Read the Palm DB name first (always 32 bytes) for fallback
        string palmName;
        using (var nameStream = File.OpenRead(filePath))
        {
            var nameBytes = new byte[Math.Min(32, nameStream.Length)];
            nameStream.ReadExactly(nameBytes);
            palmName = Encoding.Latin1.GetString(nameBytes).TrimEnd('\0').Trim();
        }

        try
        {
            return ParseMobiMetadataCore(filePath, palmName);
        }
        catch
        {
            return new MobiRawMetadata { Title = string.IsNullOrWhiteSpace(palmName) ? null : palmName };
        }
    }

    private static MobiRawMetadata ParseMobiMetadataCore(string filePath, string palmName)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream);

        if (stream.Length < 78)
            return new MobiRawMetadata { Title = string.IsNullOrWhiteSpace(palmName) ? null : palmName };

        // ── PalmDB Header ──────────────────────────────────────────────────────
        // Bytes 0-31: database name (null-terminated, up to 32 bytes)
        var nameBytes = reader.ReadBytes(32);
        palmName = Encoding.Latin1.GetString(nameBytes).TrimEnd('\0').Trim();

        // Bytes 32-75: various Palm DB fields (skip most)
        stream.Seek(76, SeekOrigin.Begin);
        int recordCount = ReadBigEndianUInt16(reader);

        if (recordCount == 0)
            return new MobiRawMetadata { Title = string.IsNullOrWhiteSpace(palmName) ? null : palmName };

        // Read record 0 offset (each record list entry is 8 bytes: 4 offset + 4 attributes/uid)
        stream.Seek(78, SeekOrigin.Begin);
        uint record0Offset = ReadBigEndianUInt32(reader);
        uint record1Offset = recordCount > 1 ? ReadBigEndianUInt32At(stream, 78 + 8) : uint.MaxValue;
        uint record0Length = record1Offset == uint.MaxValue
            ? (uint)(stream.Length - record0Offset)
            : record1Offset - record0Offset;

        if (record0Offset >= (ulong)stream.Length || record0Length == 0)
            return new MobiRawMetadata { Title = string.IsNullOrWhiteSpace(palmName) ? null : palmName };

        stream.Seek(record0Offset, SeekOrigin.Begin);
        var record0 = reader.ReadBytes((int)Math.Min(record0Length, MaxRecordZeroLength));

        // Real-world files place the MOBI header at different positions within record 0.
        if (!TryFindMobiHeaderOffset(record0, out int mobiHeaderOffset) ||
            mobiHeaderOffset + 8 > record0.Length)
        {
            return new MobiRawMetadata { Title = string.IsNullOrWhiteSpace(palmName) ? null : palmName };
        }

        uint mobiHeaderLength = BigEndianUInt32(record0, mobiHeaderOffset + 4);
        string? fullTitle = TryReadFullTitle(record0, mobiHeaderOffset);

        // ── EXTH Header (immediately after MOBI header) ───────────────────────
        uint exthOffset = (uint)mobiHeaderOffset + mobiHeaderLength;
        if (exthOffset + 12 > record0.Length)
        {
            var title = NullIfEmpty(fullTitle) ?? (string.IsNullOrWhiteSpace(palmName) ? null : palmName);
            return new MobiRawMetadata { Title = title };
        }

        if (!record0.AsSpan((int)exthOffset, 4).SequenceEqual(ExthMagic))
        {
            var title = NullIfEmpty(fullTitle) ?? (string.IsNullOrWhiteSpace(palmName) ? null : palmName);
            return new MobiRawMetadata { Title = title };
        }

        uint exthRecordCount = BigEndianUInt32(record0, (int)exthOffset + 8);

        var meta = new MobiRawMetadata();
        var authors = new List<string>();
        var tags = new List<string>();

        uint pos = exthOffset + 12;
        for (uint i = 0; i < exthRecordCount; i++)
        {
            if (pos + 8 > record0.Length) break;

            uint recordType = BigEndianUInt32(record0, (int)pos);
            uint recordLength = BigEndianUInt32(record0, (int)pos + 4);
            if (recordLength < 8 || pos + recordLength > record0.Length) break;

            var valueBytes = record0.AsSpan((int)(pos + 8), (int)(recordLength - 8));
            var valueStr = DecodeMetadataString(valueBytes);

            switch (recordType)
            {
                case 100: // Author(s)
                    if (!string.IsNullOrWhiteSpace(valueStr)) authors.Add(valueStr);
                    break;
                case 101: // Publisher
                    meta.Publisher ??= NullIfEmpty(valueStr);
                    break;
                case 103: // Description
                    meta.Description ??= NullIfEmpty(valueStr);
                    break;
                case 104: // ISBN
                    meta.Isbn ??= NullIfEmpty(valueStr);
                    break;
                case 105: // Subject/Tags
                    if (!string.IsNullOrWhiteSpace(valueStr)) tags.Add(valueStr);
                    break;
                case 106: // Publishing date
                    if (meta.PublishedDate is null && TryParsePublishedDate(valueStr, out var dt))
                        meta.PublishedDate = dt;
                    break;
                case 503: // Updated title (preferred over PalmDB name)
                    meta.Title ??= NullIfEmpty(valueStr);
                    break;
                case 524: // Language
                    meta.Language ??= NullIfEmpty(valueStr);
                    break;
            }

            pos += recordLength;
        }

        meta.Title ??= NullIfEmpty(fullTitle) ?? (string.IsNullOrWhiteSpace(palmName) ? null : palmName);
        meta.Authors = authors.Count > 0 ? authors.ToArray() : null;
        meta.Tags = tags.Count > 0 ? tags.ToArray() : null;
        return meta;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool TryFindMobiHeaderOffset(byte[] record0, out int mobiHeaderOffset)
    {
        mobiHeaderOffset = -1;
        int maxOffset = Math.Min(record0.Length - MobiMagic.Length, 64);
        for (int i = 0; i <= maxOffset; i++)
        {
            if (record0.AsSpan(i, MobiMagic.Length).SequenceEqual(MobiMagic))
            {
                mobiHeaderOffset = i;
                return true;
            }
        }

        return false;
    }

    private static string? TryReadFullTitle(byte[] record0, int mobiHeaderOffset)
    {
        foreach (var candidate in GetFullTitleFieldOffsets(mobiHeaderOffset))
        {
            if (candidate.OffsetField + 8 > record0.Length)
                continue;

            uint fullTitleOffset = BigEndianUInt32(record0, candidate.OffsetField);
            uint fullTitleLength = BigEndianUInt32(record0, candidate.OffsetField + 4);
            if (fullTitleOffset == 0 || fullTitleLength == 0)
                continue;

            if (fullTitleOffset + fullTitleLength > record0.Length)
                continue;

            var title = DecodeMetadataString(record0.AsSpan((int)fullTitleOffset, (int)fullTitleLength));
            if (!string.IsNullOrWhiteSpace(title))
                return title;
        }

        return null;
    }

    private static IEnumerable<(int OffsetField, int LengthField)> GetFullTitleFieldOffsets(int mobiHeaderOffset)
    {
        yield return (84, 88);

        int relativeToRecord32Header = mobiHeaderOffset + 84;
        if (relativeToRecord32Header != 84)
            yield return (relativeToRecord32Header, relativeToRecord32Header + 4);
    }

    private static string DecodeMetadataString(ReadOnlySpan<byte> bytes)
    {
        string utf8 = Encoding.UTF8.GetString(bytes).Trim('\0').Trim();
        if (!utf8.Contains('\uFFFD'))
            return utf8;

        return Encoding.Latin1.GetString(bytes).Trim('\0').Trim();
    }

    private static bool TryParsePublishedDate(string value, out DateTimeOffset date)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out date);
    }

    private static uint BigEndianUInt32(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24) |
        ((uint)buffer[offset + 1] << 16) |
        ((uint)buffer[offset + 2] << 8) |
        buffer[offset + 3];

    private static ushort ReadBigEndianUInt16(BinaryReader reader)
    {
        var b = reader.ReadBytes(2);
        return (ushort)((b[0] << 8) | b[1]);
    }

    private static uint ReadBigEndianUInt32(BinaryReader reader)
    {
        var b = reader.ReadBytes(4);
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static uint ReadBigEndianUInt32At(Stream stream, long offset)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var b = new byte[4];
        stream.ReadExactly(b);
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private sealed class MobiRawMetadata
    {
        public string? Title { get; set; }
        public string[]? Authors { get; set; }
        public string? Publisher { get; set; }
        public string? Description { get; set; }
        public string? Language { get; set; }
        public string? Isbn { get; set; }
        public DateTimeOffset? PublishedDate { get; set; }
        public string[]? Tags { get; set; }
    }
}
