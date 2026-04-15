using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;
using System.Text;
using System.Text.Json;

namespace MarkItDown.Core.Converters;

public sealed partial class IpynbConverter : DocumentConverter
{
    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        if (string.Equals(streamInfo.Extension, ".ipynb", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(streamInfo.MimeType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return IsNotebook(stream);
        }

        return false;
    }

    public override async Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        var text = await StreamHelpers.ReadAllTextAsync(stream, streamInfo, cancellationToken);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        string? title = null;

        var sections = new List<string>();

        if (root.TryGetProperty("cells", out var cells) && cells.ValueKind == JsonValueKind.Array)
        {
            foreach (var cell in cells.EnumerateArray())
            {
                var cellType = cell.TryGetProperty("cell_type", out var ct) ? ct.GetString() : null;
                var source = JoinSource(cell);

                if (string.IsNullOrWhiteSpace(source))
                {
                    continue;
                }

                switch (cellType)
                {
                    case "markdown":
                        sections.Add(source);

                        // Extract title from first heading in any markdown cell
                        if (title is null)
                        {
                            foreach (var line in source.Split('\n'))
                            {
                                var trimmed = line.TrimEnd('\r');
                                if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                                {
                                    title = trimmed["# ".Length..].Trim();
                                    break;
                                }
                            }
                        }

                        break;

                    case "code":
                        sections.Add($"```python\n{source}\n```");
                        break;

                    case "raw":
                        sections.Add($"```\n{source}\n```");
                        break;
                }
            }
        }

        // Fall back to notebook-level metadata.title if no heading was found in cells
        if (title is null &&
            root.TryGetProperty("metadata", out var metadata) &&
            metadata.TryGetProperty("title", out var metaTitle) &&
            metaTitle.ValueKind == JsonValueKind.String)
        {
            var t = metaTitle.GetString();
            if (!string.IsNullOrWhiteSpace(t))
            {
                title = t;
            }
        }

        var markdown = string.Join("\n\n", sections);
        return new DocumentConverterResult(markdown, title);
    }

    private static string JoinSource(JsonElement cell)
    {
        if (!cell.TryGetProperty("source", out var source))
        {
            return string.Empty;
        }

        if (source.ValueKind == JsonValueKind.String)
        {
            return source.GetString() ?? string.Empty;
        }

        if (source.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var line in source.EnumerateArray())
            {
                if (line.ValueKind == JsonValueKind.String)
                {
                    sb.Append(line.GetString());
                }
            }

            return sb.ToString();
        }

        return string.Empty;
    }

    private static bool IsNotebook(Stream stream)
    {
        var position = stream.Position;
        try
        {
            // Read up to 4KB to check for notebook markers
            var buffer = new byte[Math.Min(4096, stream.Length - stream.Position)];
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            var preview = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            return preview.Contains("\"nbformat\"", StringComparison.Ordinal)
                && preview.Contains("\"nbformat_minor\"", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
        finally
        {
            stream.Position = position;
        }
    }
}
