namespace RequestProcessor;

/// <summary>
/// Handles a specific request type within a mediator-based dispatch pipeline.
/// Register one handler per <typeparamref name="TRequest"/> type using
/// <c>services.AddRequestHandler&lt;TRequest, THandler&gt;()</c>.
/// </summary>
public interface IRequestHandler<TRequest> where TRequest : notnull
{
    /// <summary>
    /// Processes the strongly-typed request in <paramref name="context"/>.
    /// Access the request via <c>context.Data</c>.
    /// Progress can be reported via <see cref="RequestContext.OnProgress"/>.
    /// </summary>
    ValueTask<RequestResult> HandleAsync(
        RequestContext<TRequest> context,
        CancellationToken cancellationToken);
}
