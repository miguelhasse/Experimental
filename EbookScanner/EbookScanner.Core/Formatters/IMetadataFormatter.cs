using EbookScanner.Core.Models;

namespace EbookScanner.Core.Formatters;

public interface IMetadataFormatter
{
    string Format(ScanResult result);
}
