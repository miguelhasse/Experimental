namespace RequestProcessor.Tests;

public sealed class RequestPoolEnqueueValidationTests
{
    [Fact]
    public async Task EnqueueAsync_ThrowsArgumentException_WhenRequestIdIsEmpty()
    {
        await using var f = await ServiceFactory.CreateAsync((ctx, ct) =>
            Task.FromResult(new RequestResult(ctx.RequestId, true, null)));

        var context = new RequestContext<string>("", "data");
        await Assert.ThrowsAsync<ArgumentException>(
            () => f.Service.EnqueueAsync(context, _ => Task.CompletedTask, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task EnqueueAsync_ThrowsArgumentException_WhenRequestIdExceeds256Chars()
    {
        await using var f = await ServiceFactory.CreateAsync((ctx, ct) =>
            Task.FromResult(new RequestResult(ctx.RequestId, true, null)));

        var context = new RequestContext<string>(new string('a', 257), "data");
        await Assert.ThrowsAsync<ArgumentException>(
            () => f.Service.EnqueueAsync(context, _ => Task.CompletedTask, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task EnqueueAsync_ThrowsArgumentException_WhenRequestIdContainsNewline()
    {
        await using var f = await ServiceFactory.CreateAsync((ctx, ct) =>
            Task.FromResult(new RequestResult(ctx.RequestId, true, null)));

        var context = new RequestContext<string>("id\ninjection", "data");
        await Assert.ThrowsAsync<ArgumentException>(
            () => f.Service.EnqueueAsync(context, _ => Task.CompletedTask, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task EnqueueAsync_ThrowsArgumentException_WhenRequestIdContainsTab()
    {
        await using var f = await ServiceFactory.CreateAsync((ctx, ct) =>
            Task.FromResult(new RequestResult(ctx.RequestId, true, null)));

        var context = new RequestContext<string>("valid\there", "data");
        await Assert.ThrowsAsync<ArgumentException>(
            () => f.Service.EnqueueAsync(context, _ => Task.CompletedTask, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task EnqueueAsync_Succeeds_WhenRequestIdIs256Chars()
    {
        var tcs = new TaskCompletionSource<RequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var f = await ServiceFactory.CreateAsync(async (ctx, ct) =>
        {
            await Task.Yield();
            return new RequestResult(ctx.RequestId, Success: true, Output: null);
        });

        var context = new RequestContext<string>(new string('a', 256), "data");
        await f.Service.EnqueueAsync(context, r => { tcs.TrySetResult(r); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(result.Success);
    }
}
