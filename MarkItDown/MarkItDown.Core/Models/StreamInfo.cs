namespace MarkItDown.Core.Models;

public sealed record StreamInfo(
    string? MimeType = null,
    string? Extension = null,
    string? Charset = null,
    string? FileName = null,
    string? LocalPath = null,
    string? Url = null)
{
    public StreamInfo Merge(StreamInfo? other)
    {
        if (other is null)
        {
            return this;
        }

        return this with
        {
            MimeType = other.MimeType ?? MimeType,
            Extension = other.Extension ?? Extension,
            Charset = other.Charset ?? Charset,
            FileName = other.FileName ?? FileName,
            LocalPath = other.LocalPath ?? LocalPath,
            Url = other.Url ?? Url
        };
    }
}
