using Microsoft.Extensions.DependencyInjection;
using System.Collections.Frozen;

namespace RequestProcessor;

/// <summary>
/// An <see cref="IRequestDispatcher"/> that routes each request to the
/// <see cref="IRequestHandler{TRequest}"/> whose type parameter matches
/// the generic argument of the <see cref="RequestContext{TData}"/> passed in.
/// </summary>
public sealed class MediatorRequestDispatcher : IRequestDispatcher
{
    private readonly FrozenDictionary<Type, IRequestHandlerWrapper> handlers;

    public MediatorRequestDispatcher(IServiceProvider sp)
        : this(sp, sp.GetService<RequestHandlerRegistry>() ?? new RequestHandlerRegistry())
    {
    }

    internal MediatorRequestDispatcher(IServiceProvider sp, RequestHandlerRegistry registry)
    {
        handlers = registry.GetRequestTypes().ToFrozenDictionary(
            requestType => requestType,
            requestType => sp.GetKeyedService<IRequestHandlerWrapper>(requestType) ?? ThrowNoHandlerRegistered(requestType));
    }

    public ValueTask<RequestResult> DispatchAsync(RequestContext context, CancellationToken cancellationToken)
    {
        var requestType = context.RequestDataType;

        var wrapper = handlers.TryGetValue(requestType, out var cachedWrapper)
            ? cachedWrapper
            : ThrowNoHandlerRegistered(requestType);

        return wrapper.HandleAsync(context, cancellationToken);
    }

    private static IRequestHandlerWrapper ThrowNoHandlerRegistered(Type requestType)
        => throw new InvalidOperationException(
            $"No handler registered for request type '{requestType.FullName}'. " +
            $"Register it with services.AddRequestHandler<TRequest, THandler>().");
}

internal sealed class RequestHandlerRegistry
{
    private readonly Lock gate = new();
    private readonly HashSet<Type> requestTypes = [];

    public void Add(Type requestType)
    {
        lock (gate)
        {
            requestTypes.Add(requestType);
        }
    }

    public Type[] GetRequestTypes()
    {
        lock (gate)
        {
            return [.. requestTypes];
        }
    }
}

internal interface IRequestHandlerWrapper
{
    ValueTask<RequestResult> HandleAsync(RequestContext context, CancellationToken cancellationToken);
}

internal sealed class RequestHandlerWrapper<TRequest>(IRequestHandler<TRequest> inner) : IRequestHandlerWrapper
    where TRequest : notnull
{
    public ValueTask<RequestResult> HandleAsync(RequestContext context, CancellationToken cancellationToken)
        => inner.HandleAsync((RequestContext<TRequest>)context, cancellationToken);
}
