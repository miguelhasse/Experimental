using MarkItDown.Core.Models;
using System.Text;

namespace MarkItDown.Core.Utilities;

internal static class MimeHelpers
{
    private static readonly Dictionary<string, string> ExtensionToMime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain",
        [".text"] = "text/plain",
        [".md"] = "text/markdown",
        [".markdown"] = "text/markdown",
        [".json"] = "application/json",
        [".jsonl"] = "application/json",
        [".csv"] = "text/csv",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".xml"] = "application/xml",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".epub"] = "application/epub+zip",
        [".chm"] = "application/vnd.ms-htmlhelp",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".webp"] = "image/webp"
    };

    public static string? GuessMimeType(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var normalized = extension.StartsWith('.') ? extension : "." + extension;
        return ExtensionToMime.GetValueOrDefault(normalized);
    }

    public static string? GuessExtension(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return null;
        }

        var extension = Path.GetExtension(fileNameOrPath);
        return string.IsNullOrWhiteSpace(extension) ? null : extension;
    }

    public static StreamInfo Normalize(StreamInfo streamInfo)
    {
        var fileName = streamInfo.FileName
            ?? Path.GetFileName(streamInfo.LocalPath)
            ?? GetFileNameFromUrl(streamInfo.Url);

        var extension = streamInfo.Extension
            ?? GuessExtension(fileName)
            ?? GuessExtension(streamInfo.LocalPath)
            ?? GuessExtension(streamInfo.Url);

        var mimeType = streamInfo.MimeType ?? GuessMimeType(extension);
        var charset = streamInfo.Charset;
        if (charset is null && mimeType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true)
        {
            charset = Encoding.UTF8.WebName;
        }

        return streamInfo with
        {
            FileName = fileName,
            Extension = extension,
            MimeType = mimeType,
            Charset = charset
        };
    }

    public static string? GetFileNameFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.IsFile)
        {
            return Path.GetFileName(uri.LocalPath);
        }

        var candidate = Path.GetFileName(uri.AbsolutePath);
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }
}
