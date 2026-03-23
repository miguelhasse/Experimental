namespace MarkItDown.Core.Models;

public sealed class DocumentConverterResult
{
    public DocumentConverterResult(string markdown, string? title = null)
    {
        Markdown = markdown ?? string.Empty;
        Title = title;
    }

    public string Markdown { get; }

    public string? Title { get; }

    public override string ToString()
    {
        return Markdown;
    }
}
