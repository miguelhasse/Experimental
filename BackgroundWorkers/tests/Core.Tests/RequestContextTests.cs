namespace RequestProcessor.Tests;

public class RequestContextTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var ctx = new RequestContext<string>("req-1", "hello");

        Assert.Equal("req-1", ctx.RequestId);
        Assert.Equal("hello", ctx.Data);
        Assert.Equal(RequestPriority.Normal, ctx.Priority);
    }

    [Fact]
    public void Constructor_PriorityDefaults_ToNormal()
    {
        var ctx = new RequestContext<string>("req-1", "hello");

        Assert.Equal(RequestPriority.Normal, ctx.Priority);
    }

    [Fact]
    public void Constructor_PartitionKeyDefaults_ToNull()
    {
        var ctx = new RequestContext<string>("req-1", "hello");

        Assert.Null(ctx.PartitionKey);
    }

    [Fact]
    public void Constructor_ExplicitPriority_IsPreserved()
    {
        var hi = new RequestContext<string>("r", "p", Priority: RequestPriority.High);
        var lo = new RequestContext<string>("r", "p", Priority: RequestPriority.Low);

        Assert.Equal(RequestPriority.High, hi.Priority);
        Assert.Equal(RequestPriority.Low, lo.Priority);
    }

    [Fact]
    public void Constructor_OnProgress_IsPreserved()
    {
        RequestProgressReporter reporter = (pct, msg, delta) => { };
        var ctx = new RequestContext<string>("req-1", "hello", OnProgress: reporter);

        Assert.Same(reporter, ctx.OnProgress);
    }

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var a = new RequestContext<string>("id", "data");
        var b = new RequestContext<string>("id", "data");

        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentRequestId_AreNotEqual()
    {
        var a = new RequestContext<string>("id-1", "data");
        var b = new RequestContext<string>("id-2", "data");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentPriority_AreNotEqual()
    {
        var a = new RequestContext<string>("id", "data", Priority: RequestPriority.High);
        var b = new RequestContext<string>("id", "data", Priority: RequestPriority.Low);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void With_Priority_ProducesNewInstance()
    {
        var original = new RequestContext<string>("req-1", "data");
        var elevated = original with { Priority = RequestPriority.High };

        Assert.Equal(RequestPriority.Normal, original.Priority);
        Assert.Equal(RequestPriority.High, elevated.Priority);
    }

    [Fact]
    public void With_Data_ProducesNewInstance()
    {
        var original = new RequestContext<string>("req-1", "original-data");
        var updated = original with { Data = "new-data" };

        Assert.Equal("original-data", original.Data);
        Assert.Equal("new-data", updated.Data);
        Assert.NotSame(original, updated);
    }

    [Fact]
    public void GenericSubtype_IsAssignableToBase()
    {
        var ctx = new RequestContext<string>("req-1", "hello");

        Assert.IsAssignableFrom<RequestContext>(ctx);
    }
}
