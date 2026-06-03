using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;

namespace RequestProcessor.Benchmarks;

/// <summary>
/// Shared BenchmarkDotNet configuration.
/// Apply with <c>[Config(typeof(BenchmarkConfig))]</c> on each benchmark class.
/// <para>
/// Uses <see cref="Job.Short"/> (1 launch, 3 warmup, 3 measurement iterations) so the suite
/// completes in a few minutes. Switch to <see cref="Job.Default"/> for stable, publishable numbers.
/// </para>
/// </summary>
public sealed class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddJob(Job.ShortRun.WithId("ShortRun"));
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}
