using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace MarkItDown.Core.Converters;

public sealed partial class PdfConverter : DocumentConverter
{
    // Words whose Y-center differ by less than this fraction of average word height are on the same line.
    private const double LineGroupingTolerance = 0.5;

    // A vertical gap larger than this multiple of average line height signals a new paragraph.
    private const double ParagraphGapMultiplier = 1.5;

    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        return streamInfo.Extension is ".pdf"
            || string.Equals(streamInfo.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase);
    }

    public override async Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        using var document = PdfDocument.Open(stream);
        var nl = Environment.NewLine;
        var sections = new List<string>();

        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var wordList = page.GetWords().ToList();
            string pageText;
            try
            {
                if (TryExtractTabularContent(wordList, out var table) && table is not null)
                    pageText = table;
                else
                    pageText = ReconstructPageText(wordList);
            }
            catch
            {
                pageText = ReconstructPageText(wordList);
            }

            var imageCaptions = await ExtractImageCaptionsAsync(page, context, cancellationToken);

            var sb = new StringBuilder();
            sb.Append($"## Page {page.Number}{nl}{nl}");
            sb.Append(pageText);

            if (imageCaptions.Count > 0)
            {
                sb.Append(nl);
                foreach (var caption in imageCaptions)
                {
                    sb.Append(nl);
                    sb.Append(caption);
                }
            }

            sections.Add(sb.ToString().Trim());
        }

        return new DocumentConverterResult(string.Join(nl + nl, sections));
    }

    private static async Task<List<string>> ExtractImageCaptionsAsync(
        Page page,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken)
    {
        var captions = new List<string>();

        if (context.LlmClient is not IChatClient llmClient)
            return captions;

        foreach (var image in page.GetImages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReadOnlyMemory<byte> imageBytes;
            string mimeType;

            // Prefer raw JPEG bytes when the image stream IS a JPEG (DCTDecode filter).
            if (IsJpeg(image.RawBytes))
            {
                imageBytes = image.RawBytes.ToArray();
                mimeType = "image/jpeg";
            }
            else if (image.TryGetPng(out var pngBytes) && pngBytes is { Length: > 0 })
            {
                imageBytes = pngBytes;
                mimeType = "image/png";
            }
            else
            {
                continue; // Cannot represent this image; skip.
            }

            var caption = await LlmHelpers.CaptionImageAsync(
                llmClient, imageBytes, mimeType,
                context.LlmModel, context.LlmPrompt, cancellationToken);

            if (caption is not null)
                captions.Add($"[Description: {caption.Trim()}]");
        }

        return captions;
    }

    private static bool IsJpeg(IReadOnlyList<byte> bytes) =>
        bytes.Count >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private static string ReconstructPageText(IEnumerable<Word> words)
    {
        var wordList = words.ToList();
        if (wordList.Count == 0)
            return string.Empty;

        // Compute tolerance from average word height.
        double avgWordHeight = wordList.Average(w => w.BoundingBox.Height);
        double lineTolerance = avgWordHeight * LineGroupingTolerance;

        // Sort words top-to-bottom (PdfPig Y increases upward, so higher Y = higher on page).
        var sorted = wordList.OrderByDescending(w => w.BoundingBox.Centroid.Y).ToList();

        // Group words into lines by Y-center proximity.
        var lines = new List<List<Word>>();
        foreach (var word in sorted)
        {
            double wordY = word.BoundingBox.Centroid.Y;
            var matched = lines.LastOrDefault(
                line => Math.Abs(line[0].BoundingBox.Centroid.Y - wordY) <= lineTolerance);

            if (matched is not null)
                matched.Add(word);
            else
                lines.Add([word]);
        }

        // Within each line sort left-to-right, then produce line strings.
        var lineStrings = lines
            .Select(line => string.Join(" ", line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))
            .ToList();

        if (lineStrings.Count == 0)
            return string.Empty;

        // Compute average line height for paragraph-gap detection.
        double avgLineHeight = lines.Average(
            line => line.Max(w => w.BoundingBox.Top) - line.Min(w => w.BoundingBox.Bottom));
        double paragraphGapThreshold = avgLineHeight * ParagraphGapMultiplier;

        // Assemble output, inserting blank lines at paragraph boundaries.
        var sb = new System.Text.StringBuilder();
        sb.Append(lineStrings[0]);

        for (int i = 1; i < lines.Count; i++)
        {
            double prevLineBottom = lines[i - 1].Min(w => w.BoundingBox.Bottom);
            double currLineTop = lines[i].Max(w => w.BoundingBox.Top);
            double gap = prevLineBottom - currLineTop; // positive when there's space between lines

            if (gap > paragraphGapThreshold)
                sb.Append(Environment.NewLine).Append(Environment.NewLine);
            else
                sb.Append(Environment.NewLine);

            sb.Append(lineStrings[i]);
        }

        return MergePartialNumberingLines(sb.ToString().Trim());
    }

    /// <summary>
    /// Merges MasterFormat partial numbering lines with the following line.
    /// A line matching <c>^\.\d+$</c> (e.g. <c>.1</c>, <c>.10</c>) is a section number whose
    /// integer prefix was rendered on the previous line; prepend it to the next line so that
    /// "3" + ".1" + "Concrete" becomes "3.1 Concrete".
    /// </summary>
    private static string MergePartialNumberingLines(string text)
    {
        if (!text.Contains('.'))
        {
            return text;
        }

        var inputLines = text.Split('\n');
        var result = new List<string>(inputLines.Length);

        for (var i = 0; i < inputLines.Length; i++)
        {
            var line = inputLines[i];
            if (PartialNumberingRegex().IsMatch(line.Trim()) && i + 1 < inputLines.Length)
            {
                // Merge: attach the partial number to the start of the next line.
                var nextLine = inputLines[i + 1];
                result.Add(line.Trim() + nextLine);
                i++; // skip the next line since it's been merged
            }
            else
            {
                result.Add(line);
            }
        }

        return string.Join('\n', result);
    }

    [GeneratedRegex(@"^\.\d+$")]
    private static partial Regex PartialNumberingRegex();

    private static bool TryExtractTabularContent(IReadOnlyList<Word> words, out string? table)
    {
        table = null;
        if (words.Count < 4)
            return false;

        // Group words into rows by Y bottom coordinate (3.0 unit tolerance).
        var sorted = words.OrderByDescending(w => w.BoundingBox.Bottom).ToList();
        var rows = new List<List<Word>>();
        foreach (var word in sorted)
        {
            double y = word.BoundingBox.Bottom;
            var matched = rows.LastOrDefault(r => Math.Abs(r[0].BoundingBox.Bottom - y) <= 3.0);
            if (matched is not null)
                matched.Add(word);
            else
                rows.Add([word]);
        }

        // Sort each row left-to-right.
        foreach (var row in rows)
            row.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));

        // Split each row into cells by X gap > 10 units.
        var cellRows = rows.Select(row =>
        {
            var cells = new List<string>();
            var currentCell = new List<Word> { row[0] };
            for (int i = 1; i < row.Count; i++)
            {
                double gap = row[i].BoundingBox.Left - row[i - 1].BoundingBox.Right;
                if (gap > 10.0)
                {
                    cells.Add(string.Join(" ", currentCell.Select(w => w.Text)));
                    currentCell = [row[i]];
                }
                else
                {
                    currentCell.Add(row[i]);
                }
            }
            cells.Add(string.Join(" ", currentCell.Select(w => w.Text)));
            return cells;
        }).ToList();

        // Decide if tabular: ≥2 rows with ≥2 cells AND ≥30% of all rows have ≥2 cells.
        int multiCellRowCount = cellRows.Count(r => r.Count >= 2);
        if (multiCellRowCount < 2 || (double)multiCellRowCount / cellRows.Count < 0.30)
            return false;

        // Mode of cells-per-row, capped at 8.
        int colCount = cellRows
            .GroupBy(r => r.Count)
            .OrderByDescending(g => g.Count())
            .First().Key;
        colCount = Math.Min(colCount, 8);

        // Build markdown table (first row = header).
        var nl = Environment.NewLine;
        var sb = new StringBuilder();

        void AppendRow(IList<string> cells)
        {
            var padded = new string[colCount];
            for (int i = 0; i < colCount; i++)
            {
                if (i < cells.Count - 1)
                    padded[i] = cells[i];
                else if (i == colCount - 1)
                    // Merge any overflow cells into the last column.
                    padded[i] = string.Join(" ", cells.Skip(i));
                else
                    padded[i] = string.Empty;
            }
            sb.Append("| ").Append(string.Join(" | ", padded)).Append(" |");
        }

        AppendRow(cellRows[0]);
        sb.Append(nl);
        sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", colCount))).Append(" |");

        for (int i = 1; i < cellRows.Count; i++)
        {
            sb.Append(nl);
            AppendRow(cellRows[i]);
        }

        table = sb.ToString();
        return true;
    }
}

