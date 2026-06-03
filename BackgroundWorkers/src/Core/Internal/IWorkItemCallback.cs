namespace RequestProcessor.Internal;

internal interface IWorkItemCallback
{
    ValueTask InvokeAsync(RequestResult result);
}

internal sealed class WorkItemCallback(RequestCompletedCallback onCompleted) : IWorkItemCallback
{
    private readonly RequestCompletedCallback _onCompleted = onCompleted ?? throw new ArgumentNullException(nameof(onCompleted));

    public ValueTask InvokeAsync(RequestResult result)
    {
        var task = _onCompleted(result);
        return task.IsCompletedSuccessfully ? ValueTask.CompletedTask : new ValueTask(task);
    }
}

internal sealed class WorkItemCallback<TState>(TState state, Func<TState, RequestResult, ValueTask> callback) : IWorkItemCallback
{
    private readonly Func<TState, RequestResult, ValueTask> _callback = callback ?? throw new ArgumentNullException(nameof(callback));

    public ValueTask InvokeAsync(RequestResult result) => _callback(state, result);
}
