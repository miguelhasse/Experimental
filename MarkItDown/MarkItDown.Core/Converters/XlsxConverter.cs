using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;

namespace MarkItDown.Core.Converters;

public sealed class XlsxConverter : DocumentConverter
{
    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        return streamInfo.Extension is ".xlsx"
            || string.Equals(
                streamInfo.MimeType,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                StringComparison.OrdinalIgnoreCase);
    }

    public override Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart;
        if (workbookPart?.Workbook?.Sheets is null)
        {
            return Task.FromResult(new DocumentConverterResult(string.Empty));
        }

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable
            ?.OfType<SharedStringItem>().ToArray();

        var sections = new List<string>();

        foreach (var sheet in workbookPart.Workbook.Sheets.OfType<Sheet>())
        {
            if (sheet.Id?.Value is null)
            {
                continue;
            }

            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id.Value);
            var sheetData = worksheetPart.Worksheet?.GetFirstChild<SheetData>();
            if (sheetData is null)
            {
                continue;
            }

            var xmlRows = sheetData.OfType<Row>().ToArray();
            if (xmlRows.Length == 0)
            {
                continue;
            }

            // Determine column count from the rightmost cell across all rows.
            var maxCol = 0;
            foreach (var row in xmlRows)
            {
                foreach (var cell in row.OfType<Cell>())
                {
                    var colIndex = GetColumnIndex(cell.CellReference?.Value);
                    if (colIndex > maxCol)
                    {
                        maxCol = colIndex;
                    }
                }
            }

            var columnCount = maxCol + 1;
            var rows = new List<IReadOnlyList<string?>>(xmlRows.Length);
            foreach (var xmlRow in xmlRows)
            {
                var cellValues = new string?[columnCount];
                foreach (var cell in xmlRow.OfType<Cell>())
                {
                    var colIndex = GetColumnIndex(cell.CellReference?.Value);
                    if (colIndex >= 0 && colIndex < columnCount)
                    {
                        cellValues[colIndex] = GetCellValue(cell, sharedStrings);
                    }
                }

                rows.Add(cellValues);
            }

            var markdownTable = MarkdownHelpers.BuildTable(rows);
            if (string.IsNullOrWhiteSpace(markdownTable))
            {
                continue;
            }

            sections.Add($"## {sheet.Name?.Value}{Environment.NewLine}{Environment.NewLine}{markdownTable}");
        }

        return Task.FromResult(new DocumentConverterResult(
            string.Join(Environment.NewLine + Environment.NewLine, sections)));
    }

    private static string GetCellValue(Cell cell, SharedStringItem[]? sharedStrings)
    {
        var value = cell.CellValue?.Text;
        if (value is null)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        var dataType = cell.DataType?.Value;

        if (dataType == CellValues.SharedString
            && int.TryParse(value, out var index)
            && sharedStrings is not null
            && (uint)index < (uint)sharedStrings.Length)
        {
            return sharedStrings[index].InnerText ?? string.Empty;
        }

        if (dataType == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        if (dataType == CellValues.Boolean)
        {
            return value == "1" ? "TRUE" : "FALSE";
        }

        return value;
    }

    /// <summary>Parses the column letters of a cell reference (e.g. "AB3") into a zero-based column index.</summary>
    private static int GetColumnIndex(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference))
        {
            return -1;
        }

        var col = 0;
        foreach (var ch in cellReference)
        {
            if (ch is >= 'A' and <= 'Z')
            {
                col = col * 26 + (ch - 'A' + 1);
            }
            else
            {
                break;
            }
        }

        return col - 1;
    }
}
