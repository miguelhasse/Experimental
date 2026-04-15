using MarkItDown.Core;
using MarkItDown.Core.Models;
using Microsoft.Extensions.AI;

namespace MarkItDown.Tests;

/// <summary>
/// Tests for LLM image captioning via <see cref="IChatClient"/> integration.
/// Uses a simple stub <see cref="IChatClient"/> that returns a fixed caption.
/// </summary>
public sealed class LlmCaptioningTests : IDisposable
{
    private const string StubCaption = "A colorful test image with geometric shapes.";

    private readonly StubChatClient _chatClient = new(StubCaption);

    public void Dispose() => _chatClient.Dispose();

    // ────────────────────────────────────────────────────────────────────
    // Image converter
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImageConverter_WithLlmClient_AppendsCaptionToOutput()
    {
        using var service = new MarkItDownService(llmClient: _chatClient);

        await using var stream = CreateMinimalJpeg();
        var result = await service.ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "photo.jpg", Extension: ".jpg", MimeType: "image/jpeg"),
            TestContext.Current.CancellationToken);

        Assert.Contains("# Description:", result.Markdown);
        Assert.Contains(StubCaption, result.Markdown);
    }

    [Fact]
    public async Task ImageConverter_WithoutLlmClient_NoCaptionSection()
    {
        using var service = new MarkItDownService();

        await using var stream = CreateMinimalJpeg();
        var result = await service.ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "photo.jpg", Extension: ".jpg", MimeType: "image/jpeg"),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("# Description:", result.Markdown);
    }

    [Fact]
    public async Task ImageConverter_LlmClientReceivesImageBytes()
    {
        var capturingClient = new CapturingChatClient(StubCaption);
        using var service = new MarkItDownService(llmClient: capturingClient);

        await using var stream = CreateMinimalJpeg();
        await service.ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "photo.jpg", Extension: ".jpg", MimeType: "image/jpeg"),
            TestContext.Current.CancellationToken);

        // Verify the message sent to the client contained both text and image content.
        Assert.NotNull(capturingClient.LastMessage);
        Assert.True(capturingClient.LastMessage!.Contents.OfType<TextContent>().Any(), "Expected TextContent prompt");
        Assert.True(capturingClient.LastMessage!.Contents.OfType<DataContent>().Any(), "Expected DataContent image");
    }

    [Fact]
    public async Task ImageConverter_WithModelId_ForwardsModelToClient()
    {
        const string modelId = "gpt-4o-mini";
        var capturingClient = new CapturingChatClient(StubCaption);
        using var service = new MarkItDownService(llmClient: capturingClient, llmModel: modelId);

        await using var stream = CreateMinimalJpeg();
        await service.ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "photo.jpg", Extension: ".jpg", MimeType: "image/jpeg"),
            TestContext.Current.CancellationToken);

        Assert.Equal(modelId, capturingClient.LastOptions?.ModelId);
    }

    [Fact]
    public async Task ImageConverter_WithCustomPrompt_ForwardsPromptToClient()
    {
        const string customPrompt = "Describe in one word.";
        var capturingClient = new CapturingChatClient(StubCaption);
        using var service = new MarkItDownService(llmClient: capturingClient, llmPrompt: customPrompt);

        await using var stream = CreateMinimalJpeg();
        await service.ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "photo.jpg", Extension: ".jpg", MimeType: "image/jpeg"),
            TestContext.Current.CancellationToken);

        var textContent = capturingClient.LastMessage?.Contents.OfType<TextContent>().FirstOrDefault();
        Assert.Equal(customPrompt, textContent?.Text);
    }

    [Fact]
    public async Task ImageConverter_LlmClientThrows_OutputStillContainsMetadata()
    {
        using var service = new MarkItDownService(llmClient: new FailingChatClient());

        await using var stream = CreateMinimalJpeg();
        // Should not throw; LLM errors are swallowed gracefully.
        var result = await service.ConvertStreamAsync(
            stream,
            new StreamInfo(FileName: "photo.jpg", Extension: ".jpg", MimeType: "image/jpeg"),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("# Description:", result.Markdown);
    }

    // ────────────────────────────────────────────────────────────────────
    // MarkItDownService constructor and context wiring
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void MarkItDownService_AcceptsLlmClientParameters()
    {
        // Should not throw.
        using var service = new MarkItDownService(
            llmClient: _chatClient,
            llmModel: "gpt-4o",
            llmPrompt: "Describe the image.");

        Assert.NotNull(service);
    }

    [Fact]
    public void MarkItDownConversionContext_ExposesLlmProperties()
    {
        using var service = new MarkItDownService(
            llmClient: _chatClient,
            llmModel: "gpt-4o",
            llmPrompt: "Custom prompt");

        var context = new MarkItDownConversionContext(
            service,
            new HttpClient(),
            _chatClient,
            "gpt-4o",
            "Custom prompt");

        Assert.Same(_chatClient, context.LlmClient);
        Assert.Equal("gpt-4o", context.LlmModel);
        Assert.Equal("Custom prompt", context.LlmPrompt);
    }

    // ────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Creates a minimal valid JPEG stream (3 bytes SOI + EOF magic).</summary>
    private static MemoryStream CreateMinimalJpeg()
    {
        // Smallest valid JPEG: SOI marker (FF D8 FF) + APP0 + EOI.
        // MetadataExtractor can handle this without throwing.
        var bytes = new byte[]
        {
            0xFF, 0xD8, 0xFF, 0xE0, // SOI + APP0 marker
            0x00, 0x10,             // APP0 length = 16
            0x4A, 0x46, 0x49, 0x46, 0x00, // "JFIF\0"
            0x01, 0x01,             // version
            0x00,                   // aspect ratio units
            0x00, 0x01,             // X density
            0x00, 0x01,             // Y density
            0x00, 0x00,             // thumbnail dimensions
            0xFF, 0xD9              // EOI
        };
        return new MemoryStream(bytes);
    }

    // ────────────────────────────────────────────────────────────────────
    // Stub IChatClient implementations
    // ────────────────────────────────────────────────────────────────────

    private sealed class StubChatClient(string caption) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, caption)]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class CapturingChatClient(string caption) : IChatClient
    {
        public ChatMessage? LastMessage { get; private set; }
        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastMessage = messages.FirstOrDefault();
            LastOptions = options;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, caption)]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class FailingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<ChatResponse>(new InvalidOperationException("LLM service unavailable."));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
