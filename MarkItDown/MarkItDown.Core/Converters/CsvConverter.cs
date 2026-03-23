using CsvHelper;
using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;
using System.Globalization;

namespace MarkItDown.Core.Converters;

public sealed class CsvConverter : DocumentConverter
{
    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        return streamInfo.Extension is ".csv"
            || streamInfo.MimeType?.StartsWith("text/csv", StringComparison.OrdinalIgnoreCase) == true
            || streamInfo.MimeType?.StartsWith("application/csv", StringComparison.OrdinalIgnoreCase) == true;
    }

    public override async Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        var text = await StreamHelpers.ReadAllTextAsync(stream, streamInfo, cancellationToken);
        using var stringReader = new StringReader(text);
        using var csv = new CsvReader(stringReader, CultureInfo.InvariantCulture);

        var rows = new List<IReadOnlyList<string?>>();
        while (await csv.ReadAsync())
        {
            rows.Add(csv.Parser.Record?.Cast<string?>().ToArray() ?? []);
        }

        return new DocumentConverterResult(MarkdownHelpers.BuildTable(rows));
    }
}
