using System.Threading.Channels;

namespace RequestProcessor.Tests;

public class RequestPoolOptionsTests
{
    [Fact]
    public void MaxConcurrency_DefaultsTo_ProcessorCount()
    {
        var opts = new RequestPoolOptions();
        Assert.Equal(Environment.ProcessorCount, opts.MaxConcurrency);
    }

    [Fact]
    public void BoundedCapacity_DefaultsTo_1000()
    {
        var opts = new RequestPoolOptions();
        Assert.Equal(1_000, opts.BoundedCapacity);
    }

    [Fact]
    public void SectionName_IsRequestPool()
    {
        Assert.Equal("RequestPool", RequestPoolOptions.SectionName);
    }

    [Fact]
    public void Properties_CanBeOverridden()
    {
        var opts = new RequestPoolOptions { MaxConcurrency = 8, BoundedCapacity = 500 };

        Assert.Equal(8, opts.MaxConcurrency);
        Assert.Equal(500, opts.BoundedCapacity);
    }

    [Fact]
    public void TaskSchedulerFactory_DefaultsToNull()
    {
        var opts = new RequestPoolOptions();
        Assert.Null(opts.TaskSchedulerFactory);
    }

    [Fact]
    public void NewOptions_HaveExpectedDefaults()
    {
        var opts = new RequestPoolOptions();

        Assert.Equal(BoundedChannelFullMode.Wait, opts.FullMode);
        Assert.Null(opts.OnItemDropped);
        Assert.False(opts.SingleWriter);
        Assert.Equal(Timeout.InfiniteTimeSpan, opts.DrainTimeout);
        Assert.False(opts.PartitionFairnessEnabled);
        Assert.Null(opts.PartitionCapacity);
        Assert.Equal(TimeSpan.FromMinutes(5), opts.PartitionIdleEvictionThreshold);
        Assert.Equal(1, opts.MaxDispatchAttempts);
        Assert.Null(opts.ShouldRetry);
        Assert.Null(opts.OnDeadLetter);
        Assert.Equal(TimeSpan.Zero, opts.RetryBackoff);
        Assert.Null(opts.PriorityAgingThreshold);
        Assert.Equal(TimeSpan.FromSeconds(5), opts.PriorityAgingScanInterval);
        Assert.Null(opts.TimeProvider);
    }

    [Fact]
    public void RetryOptions_CanBeSet()
    {
        var shouldRetry = new Func<Exception, int, bool>((_, _) => true);
        var onDeadLetter = new Action<RequestContext, Exception>((_, _) => { });
        var opts = new RequestPoolOptions
        {
            MaxDispatchAttempts = 3,
            ShouldRetry = shouldRetry,
            OnDeadLetter = onDeadLetter,
            RetryBackoff = TimeSpan.FromMilliseconds(25)
        };

        Assert.Equal(3, opts.MaxDispatchAttempts);
        Assert.Same(shouldRetry, opts.ShouldRetry);
        Assert.Same(onDeadLetter, opts.OnDeadLetter);
        Assert.Equal(TimeSpan.FromMilliseconds(25), opts.RetryBackoff);
    }

    [Fact]
    public void TaskSchedulerFactory_CanBeSetAndReturnsScheduler()
    {
        var scheduler = TaskScheduler.Default;
        var opts = new RequestPoolOptions
        {
            TaskSchedulerFactory = _ => scheduler
        };

        Assert.NotNull(opts.TaskSchedulerFactory);
        Assert.Same(scheduler, opts.TaskSchedulerFactory(RequestPriority.High));
        Assert.Same(scheduler, opts.TaskSchedulerFactory(RequestPriority.Normal));
        Assert.Same(scheduler, opts.TaskSchedulerFactory(RequestPriority.Low));
    }
}

public class RequestPoolOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(RequestPoolOptions options) =>
        new RequestPoolOptionsValidator().Validate(name: null, options);

    [Fact]
    public void ValidOptions_ReturnsSuccess()
    {
        var result = Validate(new RequestPoolOptions());
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxDispatchAttempts_LessThanOne_Fails(int value)
    {
        var result = Validate(new RequestPoolOptions { MaxDispatchAttempts = value });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(RequestPoolOptions.MaxDispatchAttempts)));
    }

    [Fact]
    public void RetryBackoff_Negative_Fails()
    {
        var result = Validate(new RequestPoolOptions { RetryBackoff = TimeSpan.FromMilliseconds(-1) });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(RequestPoolOptions.RetryBackoff)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxConcurrency_ZeroOrNegative_Fails(int value)
    {
        var result = Validate(new RequestPoolOptions { MaxConcurrency = value });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(RequestPoolOptions.MaxConcurrency)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BoundedCapacity_ZeroOrNegative_Fails(int value)
    {
        var result = Validate(new RequestPoolOptions { BoundedCapacity = value });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(RequestPoolOptions.BoundedCapacity)));
    }

    [Fact]
    public void PriorityWeights_Null_Fails()
    {
        var result = Validate(new RequestPoolOptions { PriorityWeights = null! });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(RequestPoolOptions.PriorityWeights)));
    }

    [Theory]
    [InlineData(new int[] { })]
    [InlineData(new int[] { 1, 2 })]
    [InlineData(new int[] { 1, 2, 3, 4 })]
    public void PriorityWeights_WrongLength_Fails(int[] weights)
    {
        var result = Validate(new RequestPoolOptions { PriorityWeights = weights });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(RequestPoolOptions.PriorityWeights)));
    }

    [Theory]
    [InlineData(new int[] { 0, 3, 5 })]
    [InlineData(new int[] { 1, -1, 5 })]
    [InlineData(new int[] { 0, 0, 0 })]
    public void PriorityWeights_NonPositiveValue_Fails(int[] weights)
    {
        var result = Validate(new RequestPoolOptions { PriorityWeights = weights });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(RequestPoolOptions.PriorityWeights)));
    }

    [Fact]
    public void MultipleInvalidProperties_ReportsAllErrors()
    {
        var result = Validate(new RequestPoolOptions { MaxConcurrency = 0, BoundedCapacity = -1 });
        Assert.True(result.Failed);
        var failures = result.Failures!.ToList();
        Assert.Contains(failures, f => f.Contains(nameof(RequestPoolOptions.MaxConcurrency)));
        Assert.Contains(failures, f => f.Contains(nameof(RequestPoolOptions.BoundedCapacity)));
    }

    [Fact]
    public void Validate_ReturnsFailure_WhenDispatchTimeoutMsIsZero()
    {
        var result = Validate(new RequestPoolOptions { DispatchTimeoutMs = 0 });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(RequestPoolOptions.DispatchTimeoutMs)));
    }

    [Fact]
    public void Validate_ReturnsFailure_WhenDispatchTimeoutMsIsNegativeTwo()
    {
        var result = Validate(new RequestPoolOptions { DispatchTimeoutMs = -2 });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(RequestPoolOptions.DispatchTimeoutMs)));
    }

    [Fact]
    public void Validate_Succeeds_WhenDispatchTimeoutMsIsInfinite()
    {
        var result = Validate(new RequestPoolOptions { DispatchTimeoutMs = Timeout.Infinite });
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_Succeeds_WhenDispatchTimeoutMsIsPositive()
    {
        var result = Validate(new RequestPoolOptions { DispatchTimeoutMs = 5000 });
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ReturnsFailure_WhenFullModeIsUndefined()
    {
        var result = Validate(new RequestPoolOptions { FullMode = (BoundedChannelFullMode)999 });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(RequestPoolOptions.FullMode)));
    }

    [Fact]
    public void Validate_ReturnsFailure_WhenDrainTimeoutIsInvalidNegative()
    {
        var result = Validate(new RequestPoolOptions { DrainTimeout = TimeSpan.FromMilliseconds(-2) });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(RequestPoolOptions.DrainTimeout)));
    }

    [Fact]
    public void Validate_Succeeds_WhenDrainTimeoutIsInfinite()
    {
        var result = Validate(new RequestPoolOptions { DrainTimeout = Timeout.InfiniteTimeSpan });
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ReturnsFailure_WhenPriorityAgingThresholdIsZero()
    {
        var result = Validate(new RequestPoolOptions { PriorityAgingThreshold = TimeSpan.Zero });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(RequestPoolOptions.PriorityAgingThreshold)));
    }

    [Fact]
    public void Validate_ReturnsFailure_WhenPriorityAgingScanIntervalIsZero()
    {
        var result = Validate(new RequestPoolOptions { PriorityAgingScanInterval = TimeSpan.Zero });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(RequestPoolOptions.PriorityAgingScanInterval)));
    }

    [Fact]
    public void Validate_Succeeds_WhenPriorityAgingIsConfigured()
    {
        var result = Validate(new RequestPoolOptions
        {
            PriorityAgingThreshold = TimeSpan.FromSeconds(30),
            PriorityAgingScanInterval = TimeSpan.FromSeconds(1)
        });
        Assert.True(result.Succeeded);
    }

    // ─── MaxConcurrentPerPriority ─────────────────────────────────────────────

    [Fact]
    public void MaxConcurrentPerPriority_Null_Passes()
    {
        var result = Validate(new RequestPoolOptions { MaxConcurrentPerPriority = null });
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void MaxConcurrentPerPriority_AllZero_Passes()
    {
        var result = Validate(new RequestPoolOptions { MaxConcurrentPerPriority = [0, 0, 0] });
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void MaxConcurrentPerPriority_ValidPositiveValues_Passes()
    {
        var result = Validate(new RequestPoolOptions { MaxConcurrentPerPriority = [2, 4, 8] });
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(new int[] { })]
    [InlineData(new int[] { 1, 2 })]
    [InlineData(new int[] { 1, 2, 3, 4 })]
    public void MaxConcurrentPerPriority_WrongLength_Fails(int[] caps)
    {
        var result = Validate(new RequestPoolOptions { MaxConcurrentPerPriority = caps });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(RequestPoolOptions.MaxConcurrentPerPriority)));
    }

    [Theory]
    [InlineData(new int[] { -1, 0, 0 })]
    [InlineData(new int[] { 0, -2, 0 })]
    [InlineData(new int[] { 0, 0, -1 })]
    public void MaxConcurrentPerPriority_NegativeValue_Fails(int[] caps)
    {
        var result = Validate(new RequestPoolOptions { MaxConcurrentPerPriority = caps });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains(nameof(RequestPoolOptions.MaxConcurrentPerPriority)));
    }
}
