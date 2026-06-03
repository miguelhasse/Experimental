namespace RequestProcessor.Tests.Helpers;

/// <summary>
/// Test double for <see cref="IRequestDispatcher"/> that delegates to a caller-supplied function.
/// </summary>
public sealed class FakeDispatcher(
    Func<RequestContext, CancellationToken, Task<RequestResult>> impl) : IRequestDispatcher
{
    public ValueTask<RequestResult> DispatchAsync(RequestContext context, CancellationToken cancellationToken)
        => new(impl(context, cancellationToken));
}
