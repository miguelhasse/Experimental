using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;

namespace MarkItDown.Core.Converters;

public sealed class HtmlConverter : DocumentConverter
{
    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        return streamInfo.Extension is ".html" or ".htm"
            || streamInfo.MimeType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true
            || streamInfo.MimeType?.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase) == true;
    }

    public override async Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        var html = await StreamHelpers.ReadAllTextAsync(stream, streamInfo, cancellationToken);
        return HtmlMarkdownConverter.Convert(html);
    }
}
