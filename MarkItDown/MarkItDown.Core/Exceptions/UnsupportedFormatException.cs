namespace MarkItDown.Core.Exceptions;

public sealed class UnsupportedFormatException : FileConversionException
{
    public UnsupportedFormatException(string message)
        : base(message)
    {
    }
}
