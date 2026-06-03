using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace RequestProcessor;

/// <summary>
/// Validates <see cref="RequestPoolOptions"/> at host startup via
/// <see cref="IValidateOptions{TOptions}"/>.
/// </summary>
internal sealed class RequestPoolOptionsValidator : IValidateOptions<RequestPoolOptions>
{
    public ValidateOptionsResult Validate(string? name, RequestPoolOptions options)
    {
        List<string>? errors = null;

        if (options.MaxConcurrency <= 0)
        {
            (errors ??= []).Add($"{nameof(RequestPoolOptions.MaxConcurrency)} must be greater than zero; got {options.MaxConcurrency}.");
        }

        if (options.BoundedCapacity <= 0)
        {
            (errors ??= []).Add($"{nameof(RequestPoolOptions.BoundedCapacity)} must be greater than zero; got {options.BoundedCapacity}.");
        }

        if (options.PriorityWeights is null)
        {
            (errors ??= []).Add($"{nameof(RequestPoolOptions.PriorityWeights)} must not be null.");
        }
        else if (options.PriorityWeights.Length != 3)
        {
            (errors ??= []).Add(
                $"{nameof(RequestPoolOptions.PriorityWeights)} must have exactly 3 elements " +
                $"(Low, Normal, High); got {options.PriorityWeights.Length}.");
        }
        else
        {
            for (int i = 0; i < options.PriorityWeights.Length; i++)
            {
                if (options.PriorityWeights[i] <= 0)
                {
                    (errors ??= []).Add($"All {nameof(RequestPoolOptions.PriorityWeights)} values must be greater than zero.");
                    break;
                }
            }
        }

        if (!Enum.IsDefined(options.FullMode))
        {
            (errors ??= []).Add($"{nameof(RequestPoolOptions.FullMode)} must be a defined {nameof(BoundedChannelFullMode)} value.");
        }

        if (options.DrainTimeout < TimeSpan.Zero && options.DrainTimeout != Timeout.InfiniteTimeSpan)
        {
            (errors ??= []).Add($"{nameof(RequestPoolOptions.DrainTimeout)} must be non-negative or {nameof(Timeout.InfiniteTimeSpan)}.");
        }

        if (options.DispatchTimeoutMs != Timeout.Infinite && options.DispatchTimeoutMs <= 0)
        {
            (errors ??= []).Add($"{nameof(RequestPoolOptions.DispatchTimeoutMs)} must be {Timeout.Infinite} (infinite/disabled) or a positive number of milliseconds.");
        }

        if (options.PartitionCapacity is <= 0)
        {
            (errors ??= []).Add($"{nameof(RequestPoolOptions.PartitionCapacity)} must be null or greater than zero; got {options.PartitionCapacity}.");
        }

        if (options.PartitionIdleEvictionThreshold < TimeSpan.Zero)
        {
            (errors ??= []).Add($"{nameof(RequestPoolOptions.PartitionIdleEvictionThreshold)} must be non-negative.");
        }

        if (options.MaxDispatchAttempts < 1)
        {
            (errors ??= []).Add($"{nameof(RequestPoolOptions.MaxDispatchAttempts)} must be greater than or equal to one; got {options.MaxDispatchAttempts}.");
        }

        if (options.RetryBackoff < TimeSpan.Zero)
        {
            (errors ??= []).Add($"{nameof(RequestPoolOptions.RetryBackoff)} must be non-negative.");
        }

        if (options.PriorityAgingThreshold is { } threshold && threshold <= TimeSpan.Zero)
        {
            (errors ??= []).Add($"{nameof(RequestPoolOptions.PriorityAgingThreshold)} must be greater than zero when set.");
        }

        if (options.PriorityAgingScanInterval <= TimeSpan.Zero)
        {
            (errors ??= []).Add($"{nameof(RequestPoolOptions.PriorityAgingScanInterval)} must be greater than zero.");
        }

        if (options.MaxConcurrentPerPriority is { } caps)
        {
            if (caps.Length != 3)
            {
                (errors ??= []).Add(
                    $"{nameof(RequestPoolOptions.MaxConcurrentPerPriority)} must have exactly 3 elements " +
                    $"(Low, Normal, High); got {caps.Length}.");
            }
            else
            {
                for (int i = 0; i < caps.Length; i++)
                {
                    if (caps[i] < 0)
                    {
                        (errors ??= []).Add($"All {nameof(RequestPoolOptions.MaxConcurrentPerPriority)} values must be >= 0 (use 0 for uncapped).");
                        break;
                    }
                }
            }
        }

        return errors is { Count: > 0 }
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
