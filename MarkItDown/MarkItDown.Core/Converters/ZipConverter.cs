using MarkItDown.Core.Exceptions;
using MarkItDown.Core.Models;
using System.IO.Compression;

namespace MarkItDown.Core.Converters;

public sealed class ZipConverter : DocumentConverter
{
    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        return streamInfo.Extension is ".zip"
            || string.Equals(streamInfo.MimeType, "application/zip", StringComparison.OrdinalIgnoreCase);
    }

    public override async Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var sourceName = streamInfo.Url ?? streamInfo.LocalPath ?? streamInfo.FileName ?? "archive.zip";
        var sections = new List<string> { $"Content from the zip file `{sourceName}`:" };

        foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Name)))
        {
            var safeEntryName = entry.FullName
                .Replace("..", "_", StringComparison.Ordinal)
                .Replace("\\", "/", StringComparison.Ordinal);

            try
            {
                await using var entryStream = entry.Open();
                await using var bufferedEntryStream = new MemoryStream();
                await entryStream.CopyToAsync(bufferedEntryStream, cancellationToken);
                bufferedEntryStream.Position = 0;

                var result = await context.Service.ConvertStreamAsync(
                    bufferedEntryStream,
                    new StreamInfo(
                        Extension: Path.GetExtension(entry.FullName),
                        FileName: entry.Name),
                    cancellationToken);

                sections.Add($"## File: {safeEntryName}");
                sections.Add(result.Markdown);
            }
            catch (UnsupportedFormatException)
            {
            }
            catch (FileConversionException)
            {
            }
        }

        return new DocumentConverterResult(string.Join(Environment.NewLine + Environment.NewLine, sections));
    }
}
