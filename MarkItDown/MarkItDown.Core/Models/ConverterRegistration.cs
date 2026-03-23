using MarkItDown.Core.Converters;

namespace MarkItDown.Core.Models;

public sealed record ConverterRegistration(DocumentConverter Converter, double Priority);
