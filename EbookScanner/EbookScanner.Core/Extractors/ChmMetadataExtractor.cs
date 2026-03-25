using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using EbookScanner.Core.Models;

namespace EbookScanner.Core.Extractors;

/// <summary>
/// Extracts metadata from CHM (Compiled HTML Help) files by parsing the ITSF
/// container format and reading internal metadata-bearing objects.
/// </summary>
public sealed partial class ChmMetadataExtractor : BookMetadataExtractor
{
    private const int MaxChunkLength = 65_536;
    private const int MaxObjectLength = 1_048_576;
    private const int MaxHtmlCandidateLength = 262_144;
    private const int MaxHtmlCandidates = 12;

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
            Authors: meta.Authors,
            Publisher: meta.Publisher,
            Description: meta.Description,
            Language: meta.Language,
            Isbn: meta.Isbn,
            PublishedDate: meta.PublishedDate,
            ModifiedDate: meta.ModifiedDate,
            PageCount: null,
            Tags: meta.Tags));
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
        if (!TryReadArchive(stream, out var archive))
            return new ChmRawMetadata();

        var meta = new ChmRawMetadata();

        if (TryReadObjectBytes(stream, archive, "/#SYSTEM", out var systemData))
            meta = ParseSystemFile(systemData, archive.ItsfLanguageId);
        else
            meta.Language = LcidToLanguageTag(archive.ItsfLanguageId);

        if (TryReadObjectBytes(stream, archive, "/#WINDOWS", out var windowsData) &&
            TryReadObjectBytes(stream, archive, "/#STRINGS", out var stringsData))
        {
            MergeWindowsMetadata(meta, ParseWindowsFile(windowsData, stringsData));
        }

        if (!string.IsNullOrWhiteSpace(meta.IndexFile) &&
            TryReadTextObject(stream, archive, meta.IndexFile, out var hhkText))
        {
            meta.Tags = MergeTags(meta.Tags, ExtractTagsFromHhk(hhkText));
        }

        foreach (var candidatePath in GetHtmlCandidatePaths(archive, meta))
        {
            if (!TryReadTextObject(stream, archive, candidatePath, out var html))
                continue;

            MergeHtmlMetadata(meta, ParseHtmlMetadata(html));
        }

        return meta;
    }

    private static bool TryReadArchive(Stream stream, out ChmArchive archive)
    {
        archive = default!;

        if (stream.Length < 96)
            return false;

        var header = new byte[96];
        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(header);

        if (header[0] != 'I' || header[1] != 'T' || header[2] != 'S' || header[3] != 'F')
            return false;

        uint headerLen = ReadLE32(header, 8);
        if (headerLen < 88)
            return false;

        uint itsfLangId = ReadLE32(header, 20);
        ulong dirOffset = ReadLE64(header, 0x48);
        ulong dirLen = ReadLE64(header, 0x50);

        ulong dataOffset = headerLen >= 0x60
            ? ReadLE64(header, 0x58)
            : dirOffset + dirLen;

        if ((long)dirOffset + 84 > stream.Length)
            return false;

        stream.Seek((long)dirOffset, SeekOrigin.Begin);
        var itspHeader = new byte[84];
        stream.ReadExactly(itspHeader);

        if (itspHeader[0] != 'I' || itspHeader[1] != 'T' || itspHeader[2] != 'S' || itspHeader[3] != 'P')
            return false;

        uint itspHdrLen = ReadLE32(itspHeader, 8);
        uint blockLen = ReadLE32(itspHeader, 16);
        int indexHead = ReadLE32AsInt(itspHeader, 32);

        if (blockLen == 0 || blockLen > MaxChunkLength || indexHead < 0)
            return false;

        long chunksBase = (long)dirOffset + itspHdrLen;
        var chunkData = new byte[blockLen];
        var entries = new Dictionary<string, ChmObjectEntry>(StringComparer.OrdinalIgnoreCase);
        int chunkIndex = indexHead;

        while (chunkIndex >= 0)
        {
            long chunkPos = chunksBase + (long)chunkIndex * blockLen;
            if (chunkPos + blockLen > stream.Length)
                break;

            stream.Seek(chunkPos, SeekOrigin.Begin);
            stream.ReadExactly(chunkData);

            if (chunkData[0] != 'P' || chunkData[1] != 'M' || chunkData[2] != 'G' || chunkData[3] != 'L')
                break;

            uint freeSpace = ReadLE32(chunkData, 4);
            int nextChunk = ReadLE32AsInt(chunkData, 16);

            int pos = 20;
            int endPos = (int)blockLen - (int)freeSpace;
            if (endPos < 20 || endPos > (int)blockLen)
            {
                chunkIndex = nextChunk;
                continue;
            }

            while (pos < endPos)
            {
                int nameLen = DecodeEncInt(chunkData, ref pos);
                if (nameLen < 0 || nameLen > endPos - pos)
                    break;

                string name = NormalizeInternalPath(Encoding.UTF8.GetString(chunkData, pos, nameLen));
                pos += nameLen;

                int contentSection = DecodeEncInt(chunkData, ref pos);
                long fileOffset = DecodeEncInt64(chunkData, ref pos);
                long fileLength = DecodeEncInt64(chunkData, ref pos);

                if (contentSection < 0 || fileOffset < 0 || fileLength < 0)
                    break;

                if (!entries.ContainsKey(name))
                    entries.Add(name, new ChmObjectEntry(name, contentSection, fileOffset, fileLength));
            }

            chunkIndex = nextChunk;
        }

        archive = new ChmArchive(entries, dataOffset, itsfLangId);
        return true;
    }

    private static bool TryReadObjectBytes(
        Stream stream,
        ChmArchive archive,
        string path,
        out byte[] data)
    {
        data = [];
        if (!archive.Entries.TryGetValue(NormalizeInternalPath(path), out var entry))
            return false;

        // The current managed reader only supports section 0 (uncompressed) objects.
        if (entry.ContentSection != 0 || entry.Length <= 0 || entry.Length > MaxObjectLength)
            return false;

        long absoluteOffset = (long)archive.DataOffset + entry.Offset;
        if (absoluteOffset < 0 || absoluteOffset + entry.Length > stream.Length)
            return false;

        stream.Seek(absoluteOffset, SeekOrigin.Begin);
        data = new byte[(int)entry.Length];
        stream.ReadExactly(data);
        return true;
    }

    private static bool TryReadTextObject(
        Stream stream,
        ChmArchive archive,
        string path,
        out string text)
    {
        text = string.Empty;
        if (!TryReadObjectBytes(stream, archive, path, out var data))
            return false;

        text = DecodeText(data);
        return true;
    }

    private static ChmRawMetadata ParseSystemFile(byte[] data, uint itsfLangId)
    {
        var meta = new ChmRawMetadata();
        if (data.Length < 4)
            return meta;

        int pos = 4;
        while (pos + 4 <= data.Length)
        {
            ushort code = ReadLE16(data, pos);
            ushort len = ReadLE16(data, pos + 2);
            pos += 4;

            if (pos + len > data.Length)
                break;

            switch (code)
            {
                case 0:
                    meta.ContentsFile ??= NormalizeInternalPathOrNull(ReadNullTerminatedString(data, pos, len));
                    break;

                case 1:
                    meta.IndexFile ??= NormalizeInternalPathOrNull(ReadNullTerminatedString(data, pos, len));
                    break;

                case 2:
                    meta.DefaultTopic ??= NormalizeInternalPathOrNull(ReadNullTerminatedString(data, pos, len));
                    break;

                case 3:
                {
                    var title = ReadNullTerminatedString(data, pos, len);
                    if (!string.IsNullOrWhiteSpace(title))
                        meta.Title ??= title.Trim();
                    break;
                }

                case 4:
                {
                    if (meta.Language is null)
                    {
                        var language = TryReadLcid(data, pos, len, 0) ?? TryReadLcid(data, pos, len, 4);
                        meta.Language = language;
                    }

                    if (meta.ModifiedDate is null && len >= 0x1C)
                        meta.ModifiedDate = ReadFileTimeAsDateTimeOffset(data, pos + 0x14);
                    break;
                }

                case 6:
                {
                    var stem = ReadNullTerminatedString(data, pos, len).Trim();
                    if (!string.IsNullOrWhiteSpace(stem))
                    {
                        if (meta.ContentsFile is null)
                            meta.ContentsFile = NormalizeInternalPathOrNull(stem + ".hhc");
                        if (meta.IndexFile is null)
                            meta.IndexFile = NormalizeInternalPathOrNull(stem + ".hhk");
                    }
                    break;
                }

                case 10:
                {
                    if (len >= 4)
                    {
                        uint raw = ReadLE32(data, pos);
                        if (meta.Language is null)
                            meta.Language = LcidToLanguageTag(raw);

                        if (meta.ModifiedDate is null && TryReadUnixTime(raw, out var timestamp))
                            meta.ModifiedDate = timestamp;
                    }
                    break;
                }
            }

            pos += len;
        }

        meta.Language ??= LcidToLanguageTag(itsfLangId);
        return meta;
    }

    private static ChmWindowsMetadata ParseWindowsFile(byte[] windowsData, byte[] stringsData)
    {
        var meta = new ChmWindowsMetadata();
        if (windowsData.Length < 8)
            return meta;

        uint entries = ReadLE32(windowsData, 0);
        uint entrySize = ReadLE32(windowsData, 4);
        if (entries == 0 || entrySize < 0x6C)
            return meta;

        long offset = 8;
        for (uint i = 0; i < entries; i++)
        {
            if (offset + entrySize > windowsData.Length)
                break;

            int entryOffset = (int)offset;
            meta.Title ??= TryReadStringOffset(stringsData, ReadLE32(windowsData, entryOffset + 0x14));
            meta.ContentsFile ??= NormalizeInternalPathOrNull(TryReadStringOffset(stringsData, ReadLE32(windowsData, entryOffset + 0x60)));
            meta.IndexFile ??= NormalizeInternalPathOrNull(TryReadStringOffset(stringsData, ReadLE32(windowsData, entryOffset + 0x64)));
            meta.DefaultTopic ??= NormalizeInternalPathOrNull(TryReadStringOffset(stringsData, ReadLE32(windowsData, entryOffset + 0x68)));
            offset += entrySize;
        }

        return meta;
    }

    private static void MergeWindowsMetadata(ChmRawMetadata target, ChmWindowsMetadata windows)
    {
        target.Title ??= windows.Title;
        target.ContentsFile ??= windows.ContentsFile;
        target.IndexFile ??= windows.IndexFile;
        target.DefaultTopic ??= windows.DefaultTopic;
    }

    private static void MergeHtmlMetadata(ChmRawMetadata target, HtmlMetadata html)
    {
        target.Title ??= html.Title;
        target.Authors ??= html.Authors;
        target.Publisher ??= html.Publisher;
        target.Description ??= html.Description;
        target.Isbn ??= html.Isbn;
        target.PublishedDate ??= html.PublishedDate;
        target.Tags = MergeTags(target.Tags, html.Tags);
    }

    private static IEnumerable<string> GetHtmlCandidatePaths(ChmArchive archive, ChmRawMetadata meta)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            var normalized = NormalizeInternalPath(path);
            if (seen.Add(normalized))
                candidates.Add(normalized);
        }

        AddCandidate(meta.DefaultTopic);

        foreach (var entry in archive.Entries.Values
                     .Where(static entry =>
                         entry.ContentSection == 0 &&
                         entry.Length is > 0 and <= MaxHtmlCandidateLength &&
                         IsHtmlPath(entry.Path))
                     .OrderBy(static entry => ScoreCandidatePath(entry.Path))
                     .ThenBy(static entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                     .Take(MaxHtmlCandidates))
        {
            AddCandidate(entry.Path);
        }

        return candidates;
    }

    private static HtmlMetadata ParseHtmlMetadata(string html)
    {
        var meta = new HtmlMetadata();
        var documentTitle = ExtractTagInnerText(html, "title");
        if (!string.IsNullOrWhiteSpace(documentTitle))
            meta.Title = documentTitle;

        var metaMap = ExtractHtmlMetaMap(html);
        meta.Authors = TrySplitAuthors(
            FirstNonEmpty(metaMap,
                "author",
                "dc.creator",
                "dcterms.creator",
                "creator"));

        meta.Publisher = FirstNonEmpty(metaMap,
            "publisher",
            "dc.publisher",
            "dcterms.publisher");

        meta.Description = FirstNonEmpty(metaMap,
            "description",
            "dc.description",
            "dcterms.description");

        meta.Tags = NormalizeList(
            SplitTagList(FirstNonEmpty(metaMap,
                "keywords",
                "dc.subject",
                "dcterms.subject",
                "subject")));

        var publishedDate = FirstNonEmpty(metaMap,
            "dcterms.issued",
            "dc.date",
            "date",
            "article:published_time",
            "pubdate");

        if (!string.IsNullOrWhiteSpace(publishedDate) &&
            DateTimeOffset.TryParse(publishedDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate))
        {
            meta.PublishedDate = parsedDate;
        }

        meta.Isbn = ExtractIsbn(StripHtmlTags(html));
        return meta;
    }

    private static Dictionary<string, string> ExtractHtmlMetaMap(string html)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in MetaTagRegex().Matches(html))
        {
            var attrs = ExtractAttributes(match.Value);
            if (!attrs.TryGetValue("content", out var content) || string.IsNullOrWhiteSpace(content))
                continue;

            var key = FirstNonEmptyAttribute(attrs, "name", "property", "http-equiv");
            if (string.IsNullOrWhiteSpace(key))
                continue;

            map[key] = WebUtility.HtmlDecode(content).Trim();
        }

        return map;
    }

    private static string[] ExtractTagsFromHhk(string hhkText)
    {
        var tags = new List<string>();
        foreach (Match match in ParamTagRegex().Matches(hhkText))
        {
            var attrs = ExtractAttributes(match.Value);
            if (!attrs.TryGetValue("name", out var name) ||
                !name.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
                !attrs.TryGetValue("value", out var value))
            {
                continue;
            }

            var tag = WebUtility.HtmlDecode(value).Trim();
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            if (tag.Contains(".htm", StringComparison.OrdinalIgnoreCase) ||
                tag.Equals("Index", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            tags.Add(tag);
        }

        return NormalizeList(tags);
    }

    private static Dictionary<string, string> ExtractAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributeRegex().Matches(tag))
        {
            string key = match.Groups["name"].Value;
            string value = match.Groups["value"].Success
                ? match.Groups["value"].Value
                : match.Groups["bare"].Value;

            attributes[key] = WebUtility.HtmlDecode(value).Trim();
        }

        return attributes;
    }

    private static string? FirstNonEmptyAttribute(
        IReadOnlyDictionary<string, string> attributes,
        params string[] keys)
    {
        foreach (string key in keys)
        {
            if (attributes.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string? FirstNonEmpty(
        IReadOnlyDictionary<string, string> values,
        params string[] keys)
    {
        foreach (string key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string[]? TrySplitAuthors(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var authors = value
            .Split([";", " and "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static author => !string.IsNullOrWhiteSpace(author))
            .ToArray();

        return authors.Length == 0 ? null : authors;
    }

    private static string[] SplitTagList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split([",", ";", "|"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .ToArray();
    }

    private static string[]? MergeTags(string[]? existing, IEnumerable<string>? additional)
    {
        var merged = NormalizeList(existing ?? [], additional ?? []);
        return merged.Length == 0 ? null : merged;
    }

    private static string[] NormalizeList(params IEnumerable<string>[] groups)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = new List<string>();

        foreach (var group in groups)
        {
            foreach (var value in group)
            {
                var normalized = value.Trim();
                if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                    continue;

                values.Add(normalized);
            }
        }

        return values.ToArray();
    }

    private static string? ExtractIsbn(string text)
    {
        foreach (Match match in IsbnRegex().Matches(text))
        {
            string normalized = NormalizeIsbn(match.Groups["isbn"].Value);
            if (normalized.Length == 10 && IsValidIsbn10(normalized))
                return normalized;
            if (normalized.Length == 13 && IsValidIsbn13(normalized))
                return normalized;
        }

        return null;
    }

    private static string NormalizeIsbn(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsDigit(c) || c is 'X' or 'x')
                sb.Append(char.ToUpperInvariant(c));
        }

        return sb.ToString();
    }

    private static bool IsValidIsbn10(string isbn)
    {
        if (isbn.Length != 10)
            return false;

        int checksum = 0;
        for (int i = 0; i < 9; i++)
        {
            if (!char.IsDigit(isbn[i]))
                return false;

            checksum += (10 - i) * (isbn[i] - '0');
        }

        checksum += isbn[9] == 'X' ? 10 : char.IsDigit(isbn[9]) ? isbn[9] - '0' : -1000;
        return checksum % 11 == 0;
    }

    private static bool IsValidIsbn13(string isbn)
    {
        if (isbn.Length != 13 || isbn.Any(static c => !char.IsDigit(c)))
            return false;

        int checksum = 0;
        for (int i = 0; i < 12; i++)
        {
            checksum += (isbn[i] - '0') * (i % 2 == 0 ? 1 : 3);
        }

        int expected = (10 - (checksum % 10)) % 10;
        return expected == isbn[12] - '0';
    }

    private static string DecodeText(byte[] data)
    {
        if (data.Length >= 2)
        {
            if (data[0] == 0xFF && data[1] == 0xFE)
                return Encoding.Unicode.GetString(data);
            if (data[0] == 0xFE && data[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(data);
        }

        if (data.Length >= 3 &&
            data[0] == 0xEF &&
            data[1] == 0xBB &&
            data[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(data);
        }

        return Encoding.UTF8.GetString(data);
    }

    private static string StripHtmlTags(string html) =>
        WebUtility.HtmlDecode(HtmlTagRegex().Replace(html, " "));

    private static string? ExtractTagInnerText(string html, string tagName)
    {
        var pattern = $@"<{tagName}\b[^>]*>(?<value>.*?)</{tagName}>";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
            return null;

        var value = StripHtmlTags(match.Groups["value"].Value).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string ReadNullTerminatedString(byte[] data, int offset, int length)
    {
        if (length <= 0 || offset < 0 || offset >= data.Length)
            return string.Empty;

        int maxLength = Math.Min(length, data.Length - offset);
        int textLength = 0;
        while (textLength < maxLength && data[offset + textLength] != 0)
            textLength++;

        return textLength == 0 ? string.Empty : Encoding.UTF8.GetString(data, offset, textLength);
    }

    private static string? TryReadStringOffset(byte[] stringsData, uint offset)
    {
        if (offset >= stringsData.Length)
            return null;

        int end = Array.IndexOf(stringsData, (byte)0, (int)offset);
        int length = (end >= 0 ? end : stringsData.Length) - (int)offset;
        if (length <= 0)
            return null;

        var value = Encoding.UTF8.GetString(stringsData, (int)offset, length).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string NormalizeInternalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = path.Replace('\\', '/').Trim();
        return normalized.StartsWith("/", StringComparison.Ordinal) ? normalized : "/" + normalized;
    }

    private static string? NormalizeInternalPathOrNull(string? path)
    {
        var normalized = NormalizeInternalPath(path);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool IsHtmlPath(string path) =>
        path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".html", StringComparison.OrdinalIgnoreCase);

    private static int ScoreCandidatePath(string path)
    {
        int score = 0;
        if (path.Count(static c => c == '/') <= 1)
            score -= 10;
        if (path.Contains("index", StringComparison.OrdinalIgnoreCase))
            score -= 5;
        if (path.Contains("default", StringComparison.OrdinalIgnoreCase))
            score -= 5;
        if (path.Contains("title", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("about", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("copyright", StringComparison.OrdinalIgnoreCase))
        {
            score -= 3;
        }

        return score;
    }

    private static string? TryReadLcid(byte[] data, int pos, int len, int lcidOffset)
    {
        if (len < lcidOffset + 4)
            return null;

        return LcidToLanguageTag(ReadLE32(data, pos + lcidOffset));
    }

    private static DateTimeOffset? ReadFileTimeAsDateTimeOffset(byte[] data, int offset)
    {
        if (offset + 8 > data.Length)
            return null;

        long fileTime = unchecked((long)ReadLE64(data, offset));
        if (fileTime <= 0)
            return null;

        try
        {
            return DateTimeOffset.FromFileTime(fileTime);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadUnixTime(uint value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (value < 315532800u) // 1980-01-01
            return false;

        try
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds(value);
            return timestamp.Year <= 2100;
        }
        catch
        {
            return false;
        }
    }

    private static string? LcidToLanguageTag(uint lcid)
    {
        if (lcid == 0)
            return null;

        try
        {
            var name = CultureInfo.GetCultureInfo((int)lcid).Name;
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
            if ((b & 0x80) == 0)
                return result;
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
            if ((b & 0x80) == 0)
                return result;
        }

        return -1;
    }

    private static uint ReadLE32(byte[] bytes, int offset) =>
        (uint)bytes[offset] |
        ((uint)bytes[offset + 1] << 8) |
        ((uint)bytes[offset + 2] << 16) |
        ((uint)bytes[offset + 3] << 24);

    private static int ReadLE32AsInt(byte[] bytes, int offset) =>
        bytes[offset] |
        (bytes[offset + 1] << 8) |
        (bytes[offset + 2] << 16) |
        (bytes[offset + 3] << 24);

    private static ulong ReadLE64(byte[] bytes, int offset) =>
        (ulong)bytes[offset] |
        ((ulong)bytes[offset + 1] << 8) |
        ((ulong)bytes[offset + 2] << 16) |
        ((ulong)bytes[offset + 3] << 24) |
        ((ulong)bytes[offset + 4] << 32) |
        ((ulong)bytes[offset + 5] << 40) |
        ((ulong)bytes[offset + 6] << 48) |
        ((ulong)bytes[offset + 7] << 56);

    private static ushort ReadLE16(byte[] bytes, int offset) =>
        (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MetaTagRegex();

    [GeneratedRegex(@"<param\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ParamTagRegex();

    [GeneratedRegex(@"(?<name>[\w:-]+)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<bare>[^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AttributeRegex();

    [GeneratedRegex(@"ISBN(?:-1[03])?\s*[:#]?\s*(?<isbn>[0-9Xx][0-9Xx\-\s]{8,20})", RegexOptions.IgnoreCase)]
    private static partial Regex IsbnRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();

    private sealed record ChmArchive(
        IReadOnlyDictionary<string, ChmObjectEntry> Entries,
        ulong DataOffset,
        uint ItsfLanguageId);

    private sealed record ChmObjectEntry(
        string Path,
        int ContentSection,
        long Offset,
        long Length);

    private sealed class ChmWindowsMetadata
    {
        public string? Title { get; set; }
        public string? DefaultTopic { get; set; }
        public string? ContentsFile { get; set; }
        public string? IndexFile { get; set; }
    }

    private sealed class HtmlMetadata
    {
        public string? Title { get; set; }
        public string[]? Authors { get; set; }
        public string? Publisher { get; set; }
        public string? Description { get; set; }
        public string? Isbn { get; set; }
        public DateTimeOffset? PublishedDate { get; set; }
        public string[]? Tags { get; set; }
    }

    private sealed class ChmRawMetadata
    {
        public string? Title { get; set; }
        public string[]? Authors { get; set; }
        public string? Publisher { get; set; }
        public string? Description { get; set; }
        public string? Language { get; set; }
        public string? Isbn { get; set; }
        public DateTimeOffset? PublishedDate { get; set; }
        public DateTimeOffset? ModifiedDate { get; set; }
        public string[]? Tags { get; set; }
        public string? DefaultTopic { get; set; }
        public string? ContentsFile { get; set; }
        public string? IndexFile { get; set; }
    }
}
