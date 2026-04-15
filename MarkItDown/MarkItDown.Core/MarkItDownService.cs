using MarkItDown.Core.Converters;
using MarkItDown.Core.Exceptions;
using MarkItDown.Core.Models;
using MarkItDown.Core.Utilities;
using System.Net.Http.Headers;
using System.Text;

namespace MarkItDown.Core;

public sealed class MarkItDownService : IDisposable
{
    public const double PrioritySpecific = 0.0;
    public const double PriorityGeneric = 10.0;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly List<ConverterRegistration> _converters = [];
    private IReadOnlyList<ConverterRegistration>? _sortedConverters;

    public MarkItDownService(HttpClient? httpClient = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();

        if (!_httpClient.DefaultRequestHeaders.Accept.Any())
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html", 0.9));
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain", 0.8));
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.1));
        }

        RegisterBuiltIns();
    }

    public IReadOnlyList<ConverterRegistration> Converters =>
        _sortedConverters ??= _converters.OrderBy(r => r.Priority).ToArray();

    public void RegisterConverter(DocumentConverter converter, double priority = PrioritySpecific)
    {
        _converters.Add(new ConverterRegistration(converter, priority));
        _sortedConverters = null; // invalidate cache
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    public async Task<DocumentConverterResult> ConvertAsync(
        string source,
        StreamInfo? streamInfo = null,
        CancellationToken cancellationToken = default)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme is "http" or "https" or "file" or "data"))
        {
            return await ConvertUriAsync(uri, streamInfo, cancellationToken);
        }

        return await ConvertLocalAsync(source, streamInfo, cancellationToken);
    }

    public async Task<DocumentConverterResult> ConvertLocalAsync(
        string path,
        StreamInfo? streamInfo = null,
        CancellationToken cancellationToken = default)
    {
        await using var fileStream = File.OpenRead(path);
        var mergedInfo = new StreamInfo(
            LocalPath: path,
            FileName: Path.GetFileName(path),
            Extension: Path.GetExtension(path))
            .Merge(streamInfo);

        return await ConvertStreamAsync(fileStream, mergedInfo, cancellationToken);
    }

    public async Task<DocumentConverterResult> ConvertUriAsync(
        Uri uri,
        StreamInfo? streamInfo = null,
        CancellationToken cancellationToken = default)
    {
        if (uri.IsFile)
        {
            return await ConvertLocalAsync(uri.LocalPath, streamInfo?.Merge(new StreamInfo(Url: uri.ToString())), cancellationToken);
        }

        if (uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            var (mimeType, bytes) = ParseDataUri(uri.OriginalString);
            await using var dataStream = new MemoryStream(bytes);
            var dataInfo = new StreamInfo(
                Url: uri.ToString(),
                MimeType: mimeType,
                Charset: mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ? "utf-8" : null);
            return await ConvertStreamAsync(dataStream, dataInfo.Merge(streamInfo), cancellationToken);
        }

        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var responseInfo = new StreamInfo(
            Url: uri.ToString(),
            MimeType: response.Content.Headers.ContentType?.MediaType,
            Charset: response.Content.Headers.ContentType?.CharSet,
            FileName: response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName
                ?? MimeHelpers.GetFileNameFromUrl(uri.ToString()),
            Extension: Path.GetExtension(response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName
                ?? MimeHelpers.GetFileNameFromUrl(uri.ToString())));

        return await ConvertStreamAsync(responseStream, responseInfo.Merge(streamInfo), cancellationToken);
    }

    public async Task<DocumentConverterResult> ConvertStreamAsync(
        Stream stream,
        StreamInfo? streamInfo = null,
        CancellationToken cancellationToken = default)
    {
        await using var bufferedStream = await StreamHelpers.BufferAsync(stream, cancellationToken);
        var normalizedInfo = MimeHelpers.Normalize(streamInfo ?? new StreamInfo());
        var context = new MarkItDownConversionContext(this, _httpClient);

        foreach (var registration in Converters)
        {
            bufferedStream.Position = 0;
            if (!registration.Converter.Accepts(bufferedStream, normalizedInfo))
            {
                continue;
            }

            bufferedStream.Position = 0;
            try
            {
                return await registration.Converter.ConvertAsync(
                    bufferedStream,
                    normalizedInfo,
                    context,
                    cancellationToken);
            }
            catch (UnsupportedFormatException)
            {
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not FileConversionException)
            {
                throw new FileConversionException(
                    $"Conversion failed in {registration.Converter.Name}.",
                    exception);
            }
        }

        throw new UnsupportedFormatException(
            $"No converter could handle the input (extension: {normalizedInfo.Extension ?? "<unknown>"}, MIME type: {normalizedInfo.MimeType ?? "<unknown>"}).");
    }

    private void RegisterBuiltIns()
    {
        RegisterConverter(new WikipediaConverter(), PrioritySpecific);
        RegisterConverter(new YouTubeConverter(), PrioritySpecific);
        RegisterConverter(new DocxConverter(), PrioritySpecific);
        RegisterConverter(new XlsxConverter(), PrioritySpecific);
        RegisterConverter(new PptxConverter(), PrioritySpecific);
        RegisterConverter(new PdfConverter(), PrioritySpecific);
        RegisterConverter(new ImageConverter(), PrioritySpecific);
        RegisterConverter(new EpubConverter(), PrioritySpecific);
        RegisterConverter(new MobiConverter(), PrioritySpecific);
        RegisterConverter(new ChmConverter(), PrioritySpecific);
        RegisterConverter(new ZipConverter(), PriorityGeneric);
        RegisterConverter(new RssConverter(), PriorityGeneric);
        RegisterConverter(new CsvConverter(), PriorityGeneric);
        RegisterConverter(new HtmlConverter(), PriorityGeneric);
        RegisterConverter(new PlainTextConverter(), PriorityGeneric);
    }

    private static (string MimeType, byte[] Bytes) ParseDataUri(string value)
    {
        const string prefix = "data:";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsupportedFormatException("The supplied URI is not a data URI.");
        }

        var commaIndex = value.IndexOf(',');
        if (commaIndex < 0)
        {
            throw new UnsupportedFormatException("The supplied data URI is invalid.");
        }

        var header = value[prefix.Length..commaIndex];
        var payload = value[(commaIndex + 1)..];
        var segments = header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mimeType = segments.FirstOrDefault() ?? "text/plain";
        var isBase64 = segments.Any(segment => segment.Equals("base64", StringComparison.OrdinalIgnoreCase));
        var bytes = isBase64
            ? Convert.FromBase64String(payload)
            : Uri.UnescapeDataString(payload).Select(character => (byte)character).ToArray();

        return (mimeType, bytes);
    }
}
