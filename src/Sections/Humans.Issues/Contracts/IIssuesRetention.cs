namespace Humans.Issues.Contracts;

/// <summary>
/// The retention rule <c>CleanupIssuesJob</c> drives. One method, returning the number of
/// issue rows removed, so the job stays a scheduler shim and the "6 months after terminal"
/// policy lives inside the section.
/// </summary>
public interface IIssuesRetention
{
    /// <summary>
    /// Deletes issues that entered a terminal state (Resolved / WontFix / Duplicate) at
    /// least 6 months ago, along with their screenshot files. Comments cascade via the FK.
    /// </summary>
    Task<int> PurgeExpiredAsync(CancellationToken ct = default);
}
