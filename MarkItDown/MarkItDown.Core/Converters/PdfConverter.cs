using MarkItDown.Core.Models;
using UglyToad.PdfPig;

namespace MarkItDown.Core.Converters;

public sealed class PdfConverter : DocumentConverter
{
    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        return streamInfo.Extension is ".pdf"
            || string.Equals(streamInfo.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase);
    }

    public override Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        using var document = PdfDocument.Open(stream);
        var sections = document.GetPages()
            .Select(page => $"## Page {page.Number}{Environment.NewLine}{Environment.NewLine}{page.Text.Trim()}")
            .ToArray();

        return Task.FromResult(new DocumentConverterResult(string.Join(Environment.NewLine + Environment.NewLine, sections)));
    }
}
