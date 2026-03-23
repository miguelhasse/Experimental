using System.Text;

namespace MarkItDown.Core.Utilities;

internal static class MarkdownHelpers
{
    public static string EscapeInline(string? value)
    {
        return (value ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", "<br/>", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Trim();
    }

    public static string BuildTable(IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var columnCount = rows.Max(r => r.Count);
        if (columnCount == 0)
        {
            return string.Empty;
        }

        var normalizedRows = rows
            .Select(row => Enumerable.Range(0, columnCount)
                .Select(index => index < row.Count ? EscapeInline(row[index]) : string.Empty)
                .ToArray())
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine($"| {string.Join(" | ", normalizedRows[0])} |");
        builder.AppendLine($"| {string.Join(" | ", Enumerable.Repeat("---", columnCount))} |");

        foreach (var row in normalizedRows.Skip(1))
        {
            builder.AppendLine($"| {string.Join(" | ", row)} |");
        }

        return builder.ToString().TrimEnd();
    }
}
