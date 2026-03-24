using System.Text.Json;
using EbookScanner.Core.Models;

namespace EbookScanner.Core.Formatters;

public sealed class JsonFormatter : IMetadataFormatter
{
    public string Format(ScanResult result) =>
        JsonSerializer.Serialize(result, EbookScannerJsonContext.Default.ScanResult);
}
