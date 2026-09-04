namespace Humans.Email.Contracts;

/// <summary>
/// The retention rule <c>CleanupEmailOutboxJob</c> drives. The cutoff itself —
/// <c>Email:OutboxRetentionDays</c> back from now — lives inside the section, so the job
/// is the scheduler shim around it (the G5 playbook, step 6b). Before it, the job injected
/// <c>IEmailOutboxRepository</c> directly.
/// </summary>
public interface IEmailOutboxRetention
{
    /// <summary>
    /// Deletes sent messages older than the configured retention window. Returns the
    /// number of rows deleted so the job can keep logging it.
    /// </summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}
