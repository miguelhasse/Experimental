using System.Text;
using EbookScanner.Core.Models;

namespace EbookScanner.Core.Extractors;

/// <summary>
/// Extracts metadata from CHM (Compiled HTML Help) files by parsing the ITSF
/// container format and reading the embedded /#SYSTEM metadata block.
/// </summary>
public sealed class ChmMetadataExtractor : BookMetadataExtractor
{
    public override bool Accepts(string filePath) =>
        Path.GetExtension(filePath).Equals(".chm", StringComparison.OrdinalIgnoreCase);

    public override Task<BookMetadata> ExtractAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(filePath);
        var meta = ParseChmMetadata(filePath);

        return Task.FromResult(new BookMetadata(
            FilePath: filePath,
            FileName: fileInfo.Name,
            Format: "CHM",
            FileSizeBytes: fileInfo.Length,
            Title: meta.Title,
            Authors: null,
            Publisher: null,
            Description: null,
            Language: meta.Language,
            Isbn: null,
            PublishedDate: null,
            ModifiedDate: null,
            PageCount: null,
            Tags: null));
    }

    private static ChmRawMetadata ParseChmMetadata(string filePath)
    {
        try
        {
            return ParseChmMetadataCore(filePath);
        }
        catch
        {
            return new ChmRawMetadata();
        }
    }

    private static ChmRawMetadata ParseChmMetadataCore(string filePath)
    {
        using var stream = File.OpenRead(filePath);

        if (stream.Length < 96)
            return new ChmRawMetadata();

        // ── ITSF Header ──────────────────────────────────────────────────────────
        // 96 bytes covers both v2 (88 bytes) and v3 (96 bytes).
        var header = new byte[96];
        stream.ReadExactly(header);

        if (header[0] != 'I' || header[1] != 'T' || header[2] != 'S' || header[3] != 'F')
            return new ChmRawMetadata();

        uint headerLen  = ReadLE32(header, 8);
        if (headerLen < 88)
            return new ChmRawMetadata();

        uint  itsfLangId = ReadLE32(header, 20);
        ulong dirOffset  = ReadLE64(header, 0x48); // offset 72
        ulong dirLen     = ReadLE64(header, 0x50); // offset 80

        // v3 (headerLen >= 96) carries an explicit data offset at 0x58; v2 derives it.
        ulong dataOffset = headerLen >= 0x60
            ? ReadLE64(header, 0x58)               // offset 88
            : dirOffset + dirLen;

        // ── ITSP Header ──────────────────────────────────────────────────────────
        if ((long)dirOffset + 84 > stream.Length)
            return new ChmRawMetadata();

        stream.Seek((long)dirOffset, SeekOrigin.Begin);
        var itspHeader = new byte[84];
        stream.ReadExactly(itspHeader);

        if (itspHeader[0] != 'I' || itspHeader[1] != 'T' || itspHeader[2] != 'S' || itspHeader[3] != 'P')
            return new ChmRawMetadata();

        uint itspHdrLen = ReadLE32(itspHeader, 8);
        uint blockLen   = ReadLE32(itspHeader, 16);
        int  indexHead  = ReadLE32AsInt(itspHeader, 32);

        if (blockLen == 0 || blockLen > 65536 || indexHead < 0)
            return new ChmRawMetadata();

        // ── Walk PMGL Chunks ─────────────────────────────────────────────────────
        long chunksBase = (long)dirOffset + itspHdrLen;
        var  chunkData  = new byte[blockLen];
        int  chunkIndex = indexHead;

        while (chunkIndex >= 0)
        {
            long chunkPos = chunksBase + (long)chunkIndex * blockLen;
            if (chunkPos + blockLen > stream.Length)
                break;

            stream.Seek(chunkPos, SeekOrigin.Begin);
            stream.ReadExactly(chunkData);

            if (chunkData[0] != 'P' || chunkData[1] != 'M' || chunkData[2] != 'G' || chunkData[3] != 'L')
                break; // unexpected non-leaf chunk; stop

            uint freeSpace = ReadLE32(chunkData, 4);
            int  nextChunk = ReadLE32AsInt(chunkData, 16);

            int pos    = 20; // entries start immediately after the 20-byte PMGL header
            int endPos = (int)blockLen - (int)freeSpace;
            if (endPos < 20 || endPos > (int)blockLen)
            {
                chunkIndex = nextChunk;
                continue;
            }

            while (pos < endPos)
            {
                int nameLen = DecodeEncInt(chunkData, ref pos);
                if (nameLen < 0 || nameLen > endPos - pos) break;

                string name = Encoding.UTF8.GetString(chunkData, pos, nameLen);
                pos += nameLen;

                int  contentSection = DecodeEncInt(chunkData, ref pos);
                long fileOffset     = DecodeEncInt64(chunkData, ref pos);
                long fileLen        = DecodeEncInt64(chunkData, ref pos);

                if (contentSection < 0 || fileOffset < 0 || fileLen <= 0) break;

                if (name == "/#SYSTEM" && contentSection == 0)
                {
                    long absOffset = (long)dataOffset + fileOffset;
                    if (fileLen <= 65536 && absOffset + fileLen <= stream.Length)
                    {
                        stream.Seek(absOffset, SeekOrigin.Begin);
                        var sysData = new byte[(int)fileLen];
                        stream.ReadExactly(sysData);
                        return ParseSystemFile(sysData, itsfLangId);
                    }
                    return new ChmRawMetadata();
                }
            }

            chunkIndex = nextChunk;
        }

        return new ChmRawMetadata();
    }

    private static ChmRawMetadata ParseSystemFile(byte[] data, uint itsfLangId)
    {
        var meta = new ChmRawMetadata();
        if (data.Length < 4) return meta;

        int pos = 4; // skip the 4-byte version field
        while (pos + 4 <= data.Length)
        {
            ushort code = ReadLE16(data, pos);
            ushort len  = ReadLE16(data, pos + 2);
            pos += 4;

            if (pos + len > data.Length) break;

            switch (code)
            {
                case 3: // Title (null-terminated string)
                    if (meta.Title is null && len > 0)
                    {
                        int textLen = len;
                        while (textLen > 0 && data[pos + textLen - 1] == 0) textLen--;
                        if (textLen > 0)
                        {
                            var title = Encoding.UTF8.GetString(data, pos, textLen).Trim();
                            if (!string.IsNullOrWhiteSpace(title))
                                meta.Title = title;
                        }
                    }
                    break;

                case 4: // 4-byte timestamp followed by 4-byte LCID
                    if (meta.Language is null && len >= 8)
                        meta.Language = LcidToLanguageTag(ReadLE32(data, pos + 4));
                    break;

                case 10: // Direct 4-byte LCID
                    if (meta.Language is null && len >= 4)
                        meta.Language = LcidToLanguageTag(ReadLE32(data, pos));
                    break;
            }

            pos += len;
        }

        if (meta.Language is null && itsfLangId != 0)
            meta.Language = LcidToLanguageTag(itsfLangId);

        return meta;
    }

    private static string? LcidToLanguageTag(uint lcid)
    {
        if (lcid == 0) return null;
        try
        {
            var name = System.Globalization.CultureInfo.GetCultureInfo((int)lcid).Name;
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes a CHM variable-length encoded integer (big-endian, 7 bits per byte,
    /// high bit set means more bytes follow).
    /// </summary>
    private static int DecodeEncInt(byte[] data, ref int pos)
    {
        int result = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            result = (result << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) return result;
        }
        return -1;
    }

    private static long DecodeEncInt64(byte[] data, ref int pos)
    {
        long result = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            result = (result << 7) | (long)(b & 0x7F);
            if ((b & 0x80) == 0) return result;
        }
        return -1;
    }

    private static uint   ReadLE32(byte[] b, int o) =>
        (uint)b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24);

    private static int ReadLE32AsInt(byte[] b, int o) =>
        b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

    private static ulong ReadLE64(byte[] b, int o) =>
        (ulong)b[o]       | ((ulong)b[o + 1] << 8)  | ((ulong)b[o + 2] << 16) | ((ulong)b[o + 3] << 24) |
        ((ulong)b[o + 4] << 32) | ((ulong)b[o + 5] << 40) | ((ulong)b[o + 6] << 48) | ((ulong)b[o + 7] << 56);

    private static ushort ReadLE16(byte[] b, int o) =>
        (ushort)((uint)b[o] | ((uint)b[o + 1] << 8));

    private sealed class ChmRawMetadata
    {
        public string? Title    { get; set; }
        public string? Language { get; set; }
    }
}
