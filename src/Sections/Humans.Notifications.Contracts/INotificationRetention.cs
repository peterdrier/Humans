namespace Humans.Notifications.Contracts;

public interface INotificationRetention
{
    /// <summary>
    /// Resolved older than 7 days, unresolved informational older than 30, and every unresolved row of a retired
    /// source. Other actionable rows are never purged: they are real work.
    /// </summary>
    Task<(int Resolved, int StaleInformational, int RetiredSource)> PurgeExpiredAsync(
        CancellationToken ct = default);
}
