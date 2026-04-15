using Microsoft.Extensions.AI;

namespace MarkItDown.Core;

public sealed class MarkItDownConversionContext
{
    public MarkItDownConversionContext(
        MarkItDownService service,
        HttpClient httpClient,
        IChatClient? llmClient = null,
        string? llmModel = null,
        string? llmPrompt = null)
    {
        Service = service;
        HttpClient = httpClient;
        LlmClient = llmClient;
        LlmModel = llmModel;
        LlmPrompt = llmPrompt;
    }

    public MarkItDownService Service { get; }

    public HttpClient HttpClient { get; }

    /// <summary>Optional LLM client used for image captioning. Null disables LLM features.</summary>
    public IChatClient? LlmClient { get; }

    /// <summary>Optional model identifier forwarded to <see cref="LlmClient"/>.</summary>
    public string? LlmModel { get; }

    /// <summary>Optional prompt override for image captioning requests.</summary>
    public string? LlmPrompt { get; }
}

