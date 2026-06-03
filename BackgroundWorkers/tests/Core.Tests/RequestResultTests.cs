namespace RequestProcessor.Tests;

public class RequestResultTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var ex = new InvalidOperationException("oops");
        var result = new RequestResult("req-1", Success: false, Output: null, Error: ex);

        Assert.Equal("req-1", result.RequestId);
        Assert.False(result.Success);
        Assert.Null(result.Output);
        Assert.Same(ex, result.Error);
    }

    [Fact]
    public void Constructor_ErrorDefaults_ToNull()
    {
        var result = new RequestResult("req-1", Success: true, Output: "done");

        Assert.Null(result.Error);
    }

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var a = new RequestResult("id", true, "out");
        var b = new RequestResult("id", true, "out");

        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentSuccess_AreNotEqual()
    {
        var a = new RequestResult("id", true, "out");
        var b = new RequestResult("id", false, "out");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task RequestCompletedCallback_DelegateInvoked()
    {
        RequestResult? captured = null;
        RequestCompletedCallback cb = r => { captured = r; return Task.CompletedTask; };

        var result = new RequestResult("id", true, "out");
        await cb(result);

        Assert.Same(result, captured);
    }
}
