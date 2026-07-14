using System.Diagnostics;

namespace RequestProcessor;

/// <summary>
/// Result produced by the dispatcher and delivered to the completion callback.
/// </summary>
/// <param name="RequestId">Mirrors the originating <see cref="RequestContext.RequestId"/>.</param>
/// <param name="Success">Whether the request completed without error.</param>
/// <param name="Output">Optional output produced by the dispatcher.</param>
/// <param name="Error">Set when <see cref="Success"/> is <c>false</c>.</param>
[DebuggerDisplay("RequestId = {RequestId}, Success = {Success}, Error = {Error?.Message}")]
public record RequestResult(
    string RequestId,
    bool Success,
    string? Output,
    Exception? Error = null,
    object? TypedOutput = null);

/// <summary>Callback invoked once the request completes. May perform async work (e.g. notify an observer).</summary>
public delegate Task RequestCompletedCallback(RequestResult result);
