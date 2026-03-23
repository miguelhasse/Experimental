namespace MarkItDown.Core;

public sealed class MarkItDownConversionContext
{
    public MarkItDownConversionContext(MarkItDownService service, HttpClient httpClient)
    {
        Service = service;
        HttpClient = httpClient;
    }

    public MarkItDownService Service { get; }

    public HttpClient HttpClient { get; }
}
