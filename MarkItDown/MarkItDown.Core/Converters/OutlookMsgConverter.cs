using MarkItDown.Core.Models;
using OpenMcdf;
using System.Text;

namespace MarkItDown.Core.Converters;

public sealed class OutlookMsgConverter : DocumentConverter
{
    // OLE Compound Document signature
    private static ReadOnlySpan<byte> OleSignature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public override bool Accepts(Stream stream, StreamInfo streamInfo)
    {
        if (streamInfo.Extension is ".msg")
            return true;

        if (streamInfo.MimeType is not null &&
            streamInfo.MimeType.StartsWith("application/vnd.ms-outlook", StringComparison.OrdinalIgnoreCase))
            return true;

        return HasOleSignature(stream);
    }

    public override Task<DocumentConverterResult> ConvertAsync(
        Stream stream,
        StreamInfo streamInfo,
        MarkItDownConversionContext context,
        CancellationToken cancellationToken = default)
    {
        using var storage = RootStorage.Open(stream, StorageModeFlags.LeaveOpen);

        var subject = TryGetStreamString(storage, "__substg1.0_0037001F");
        var from = TryGetStreamString(storage, "__substg1.0_0C1F001F");
        var to = TryGetStreamString(storage, "__substg1.0_0E04001F");
        var body = TryGetStreamString(storage, "__substg1.0_1000001F");

        var sb = new StringBuilder();
        sb.AppendLine("# Email Message");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(from))
            sb.AppendLine($"**From:** {from}");
        if (!string.IsNullOrWhiteSpace(to))
            sb.AppendLine($"**To:** {to}");
        if (!string.IsNullOrWhiteSpace(subject))
            sb.AppendLine($"**Subject:** {subject}");

        if (!string.IsNullOrWhiteSpace(body))
        {
            sb.AppendLine();
            sb.AppendLine("## Content");
            sb.AppendLine();
            sb.Append(body.TrimEnd());
        }

        return Task.FromResult(new DocumentConverterResult(sb.ToString().TrimEnd(), subject));
    }

    private static bool HasOleSignature(Stream stream)
    {
        if (stream.Length < 8)
            return false;

        var position = stream.Position;
        try
        {
            stream.Position = 0;
            Span<byte> header = stackalloc byte[8];
            return stream.Read(header) == 8 && header.SequenceEqual(OleSignature);
        }
        finally
        {
            stream.Position = position;
        }
    }

    private static string? TryGetStreamString(RootStorage storage, string streamName)
    {
        if (!storage.TryOpenStream(streamName, out var cfbStream))
            return null;

        using (cfbStream)
        {
            using var ms = new MemoryStream();
            cfbStream.CopyTo(ms);
            var bytes = ms.ToArray();
            if (bytes.Length == 0)
                return null;

            // Try UTF-16LE first (standard for MSG string properties ending in 001F)
            try
            {
                return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            }
            catch
            {
                return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            }
        }
    }
}
