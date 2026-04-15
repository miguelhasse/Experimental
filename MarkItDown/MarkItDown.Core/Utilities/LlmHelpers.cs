using Microsoft.Extensions.AI;

namespace MarkItDown.Core.Utilities;

internal static class LlmHelpers
{
    internal const string DefaultImagePrompt = "Write a detailed caption for this image.";

    /// <summary>
    /// Sends image bytes to the LLM and returns a descriptive caption.
    /// Returns <see langword="null"/> if <paramref name="llmClient"/> is null, or on any error.
    /// </summary>
    internal static async Task<string?> CaptionImageAsync(
        IChatClient llmClient,
        ReadOnlyMemory<byte> imageBytes,
        string mimeType,
        string? modelId,
        string? promptOverride,
        CancellationToken cancellationToken)
    {
        var prompt = string.IsNullOrWhiteSpace(promptOverride) ? DefaultImagePrompt : promptOverride;

        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent(prompt),
            new DataContent(imageBytes, mimeType),
        ]);

        var options = modelId is not null ? new ChatOptions { ModelId = modelId } : null;

        try
        {
            var response = await llmClient.GetResponseAsync([message], options, cancellationToken)
                                          .ConfigureAwait(false);
            return response.Text;
        }
        catch
        {
            return null;
        }
    }
}
