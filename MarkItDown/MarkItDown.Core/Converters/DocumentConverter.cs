using MarkItDown.Core.Models;

namespace MarkItDown.Core.Converters;

public abstract class DocumentConverter
{
    public virtual string Name => GetType().Name;

    public abstract bool Accepts(Stream stream, StreamInfo streamInfo);

    public abstract Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default);
}
