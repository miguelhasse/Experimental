namespace OrleansSample;

/// <summary>
/// Grain that tracks a report through three independent lifecycle operations:
/// generate, review, and publish. Each operation dispatches its own queued request
/// and is idempotent — calling a method when the operation is already pending,
/// processing, or completed is a no-op that returns the current status.
/// </summary>
/// <remarks>
/// The grain key is the report ID (string). Per-operation state is stored in
/// <see cref="IJobTracker"/> using composite keys <c>"{reportId}:{operation}"</c>.
/// </remarks>
[Alias("Grains.ReportGrain")]
public interface IReportGrain : IGrainWithStringKey
{
    /// <summary>
    /// Dispatches a <see cref="GenerateReportRequest"/> unless the generate operation
    /// is already pending, processing, or completed.
    /// </summary>
    /// <returns>The current status of the generate operation after the call.</returns>
    [Alias("GenerateAsync")]
    Task<JobStatus> GenerateAsync();

    /// <summary>
    /// Dispatches a <see cref="ReviewReportRequest"/> unless the review operation
    /// is already pending, processing, or completed.
    /// </summary>
    /// <returns>The current status of the review operation after the call.</returns>
    [Alias("ReviewAsync")]
    Task<JobStatus> ReviewAsync();

    /// <summary>
    /// Dispatches a <see cref="PublishReportRequest"/> unless the publish operation
    /// is already pending, processing, or completed.
    /// </summary>
    /// <returns>The current status of the publish operation after the call.</returns>
    [Alias("PublishAsync")]
    Task<JobStatus> PublishAsync();

    /// <summary>Returns the status of all three operations as a single snapshot.</summary>
    [Alias("GetSummaryAsync")]
    Task<ReportSummary> GetSummaryAsync();

    /// <summary>
    /// Attempts to cancel any queued (not yet dispatched) generate, review, or publish operations.
    /// </summary>
    /// <returns>
    /// <c>true</c> if at least one queued operation was cancelled;
    /// <c>false</c> if no cancellable operations were found.
    /// </returns>
    [Alias("CancelAsync")]
    Task<bool> CancelAsync();
}
