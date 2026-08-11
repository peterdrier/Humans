namespace Humans.Notifications.Contracts;

/// <summary>
/// The retention rule <c>CleanupNotificationsJob</c> drives. One method, so the job stays a
/// scheduler shim and the three cutoffs — resolved, stale informational, retired source —
/// live inside the section beside the repository they delete through; the same carve as
/// Issues' <c>IIssuesRetention</c> and Agent's <c>IAgentConversationRetention</c>
/// (design §15 step 6b). Before it, the job injected <c>INotificationRepository</c>
/// directly, which is a job skipping the service layer.
/// </summary>
public interface INotificationRetention
{
    /// <summary>
    /// Deletes resolved notifications older than 7 days, unresolved informational ones
    /// older than 30 days, and every unresolved row of a retired source. Other actionable
    /// notifications are never auto-cleaned — they represent real work. Returns the three
    /// counts separately so the job can keep logging them separately.
    /// </summary>
    Task<(int Resolved, int StaleInformational, int RetiredSource)> PurgeExpiredAsync(
        CancellationToken ct = default);
}
