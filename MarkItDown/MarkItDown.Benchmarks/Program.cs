using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

// When run with no arguments, BenchmarkSwitcher presents an interactive menu.
// To run all benchmarks:         dotnet run -c Release -- --all
// To run a single category:      dotnet run -c Release -- --filter *Service*
// To run in quick-validate mode: dotnet run -c Release -- --job short --filter *
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args,
    ManualConfig.Create(DefaultConfig.Instance)
        .WithOptions(ConfigOptions.JoinSummary));
