namespace Humans.Notifications.Contracts;

/// <summary>
/// The two auto-resolution calls a section makes when the condition an open notification
/// was reporting has been fixed. Carved off the section's internal
/// <c>INotificationInboxService</c>, whose other nine members and every inbox read model
/// stay internal: nothing outside the section renders an inbox, it only clears one.
/// </summary>
public interface INotificationAutoResolve
{
    /// <summary>
    /// Resolves all unresolved notifications of a given source type for a user.
    /// Used for auto-resolving notifications when the underlying condition is fixed
    /// (e.g., resolving AccessSuspended notifications when consents are completed).
    /// </summary>
    Task ResolveBySourceAsync(
        Guid userId, NotificationSource source,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves all unresolved notifications matching a source + source-entity key,
    /// across every recipient. Called by the owning section when the source entity
    /// reaches a terminal state (e.g., an issue is resolved → its IssueSubmitted
    /// alerts clear). <paramref name="resolvedByUserId"/> attributes the resolution.
    /// </summary>
    Task ResolveBySourceKeyAsync(
        NotificationSource source, string sourceKey, Guid? resolvedByUserId,
        CancellationToken ct = default);
}
