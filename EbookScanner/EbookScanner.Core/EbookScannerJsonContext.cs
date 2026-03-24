using System.Text.Json.Serialization;
using EbookScanner.Core.Models;

namespace EbookScanner.Core;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ScanResult))]
[JsonSerializable(typeof(BookMetadata))]
public partial class EbookScannerJsonContext : JsonSerializerContext
{
}
