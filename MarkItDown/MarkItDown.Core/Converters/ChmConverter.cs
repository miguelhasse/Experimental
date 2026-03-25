using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;

namespace MarkItDown.Core.Converters;

/// <summary>
/// Converts CHM (Microsoft Compiled HTML Help) files to Markdown.
/// Parses the ITSF binary container, decompresses LZX-compressed HTML content, and
/// converts each HTML page using <see cref="HtmlMarkdownConverter"/>.
/// No external package dependency — uses a built-in binary parser.
/// </summary>
public sealed class ChmConverter : DocumentConverter
{
    private static readonly byte[] ItsfMagic = "ITSF"u8.ToArray();

    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        if (streamInfo.Extension is ".chm")
            return true;

        if (string.Equals(streamInfo.MimeType, "application/vnd.ms-htmlhelp",
                StringComparison.OrdinalIgnoreCase))
            return true;

        return HasItsfMagic(stream);
    }

    public override async Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        var data = await ReadAllBytesAsync(stream, cancellationToken);
        var chm  = ChmParser.Parse(data);

        var htmlFiles = GetReadingOrder(chm).ToList();
        if (htmlFiles.Count == 0)
            return new DocumentConverterResult(string.Empty, chm.Title);

        var sections = new List<string>(htmlFiles.Count);
        foreach (var path in htmlFiles)
        {
            var bytes = chm.ReadFile(path);
            if (bytes is null || bytes.Length == 0)
                continue;

            var html     = DetectAndDecode(bytes);
            var markdown = HtmlMarkdownConverter.Convert(html).Markdown;
            if (string.IsNullOrWhiteSpace(markdown))
                continue;

            // Use the filename (without directory) as a section heading.
            var heading = Path.GetFileNameWithoutExtension(path);
            sections.Add($"## {heading}{Environment.NewLine}{Environment.NewLine}{markdown}");
        }

        return new DocumentConverterResult(
            string.Join(Environment.NewLine + Environment.NewLine, sections),
            chm.Title);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool HasItsfMagic(Stream stream)
    {
        if (stream.Length < 4) return false;
        stream.Position = 0;
        Span<byte> magic = stackalloc byte[4];
        return stream.Read(magic) == 4
            && magic[0] == ItsfMagic[0]
            && magic[1] == ItsfMagic[1]
            && magic[2] == ItsfMagic[2]
            && magic[3] == ItsfMagic[3];
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        if (stream is MemoryStream ms) return ms.ToArray();
        using var buf = new MemoryStream();
        await stream.CopyToAsync(buf, ct);
        return buf.ToArray();
    }

    /// <summary>
    /// Returns HTML files in reading order.
    /// Tries the #HHC table-of-contents file first; falls back to sorted directory listing.
    /// </summary>
    private static IEnumerable<string> GetReadingOrder(ChmParser chm)
    {
        // Try to find the TOC (HHC) file. Its path is often listed in #SYSTEM
        // but we also probe common names.
        foreach (var tocName in new[] { "Table of Contents.hhc", "toc.hhc", "default.hhc" })
        {
            var tocBytes = chm.ReadFile("/" + tocName)
                        ?? chm.ReadFile(tocName);
            if (tocBytes is null) continue;

            var order = ParseHhcOrder(tocBytes, chm);
            if (order.Count > 0) return order;
        }

        // Fallback: every HTML file in the directory.
        return chm.HtmlFiles;
    }

    /// <summary>Extracts local file paths from an HHC (HTML Help Contents) file.</summary>
    private static List<string> ParseHhcOrder(byte[] hhcBytes, ChmParser chm)
    {
        var html   = DetectAndDecode(hhcBytes);
        var paths  = new List<string>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // HHC is HTML with <param name="Local" value="..."> entries.
        int idx = 0;
        while (idx < html.Length)
        {
            int paramIdx = html.IndexOf("<param", idx, StringComparison.OrdinalIgnoreCase);
            if (paramIdx < 0) break;

            int end = html.IndexOf('>', paramIdx);
            if (end < 0) break;

            var tag = html.AsSpan(paramIdx, end - paramIdx + 1).ToString();
            idx = end + 1;

            if (!tag.Contains("Local", StringComparison.OrdinalIgnoreCase)) continue;

            // Extract value="..." or value='...'
            string? path = ExtractAttrValue(tag, "value");
            if (path is null) continue;

            // Strip fragment (#anchor) if present.
            int frag = path.IndexOf('#');
            if (frag >= 0) path = path[..frag];
            if (string.IsNullOrWhiteSpace(path)) continue;

            // Normalise: ensure leading '/'.
            if (!path.StartsWith('/')) path = "/" + path;

            if (seen.Add(path) && chm.ReadFile(path) is not null)
                paths.Add(path);
        }

        return paths;
    }

    private static string? ExtractAttrValue(string tag, string attr)
    {
        // Find attr= (case-insensitive).
        int ai = tag.IndexOf(attr + "=", StringComparison.OrdinalIgnoreCase);
        if (ai < 0) return null;

        int eq = tag.IndexOf('=', ai);
        if (eq < 0 || eq + 1 >= tag.Length) return null;

        char q = tag[eq + 1];
        if (q is not ('"' or '\'')) return null;

        int start = eq + 2;
        int close = tag.IndexOf(q, start);
        return close < 0 ? null : tag[start..close];
    }

    private static string DetectAndDecode(byte[] bytes)
    {
        // Check for UTF-16 BOM.
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        // UTF-8 BOM or raw UTF-8 / Latin-1.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
