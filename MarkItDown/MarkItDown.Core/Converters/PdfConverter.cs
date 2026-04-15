using MarkItDown.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace MarkItDown.Core.Converters;

public sealed class PdfConverter : DocumentConverter
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

    public override Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        using var document = PdfDocument.Open(stream);
        var nl = Environment.NewLine;
        var sections = document.GetPages()
            .Select(page =>
            {
                var pageText = ReconstructPageText(page.GetWords());
                return $"## Page {page.Number}{nl}{nl}{pageText}";
            })
            .ToArray();

        return Task.FromResult(new DocumentConverterResult(string.Join(nl + nl, sections)));
    }

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

        return sb.ToString().Trim();
    }
}
