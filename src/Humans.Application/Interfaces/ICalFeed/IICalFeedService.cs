namespace Humans.Application.Interfaces.ICalFeed;

/// <summary>
/// Orchestrator for the personal iCal feed — fans out over every
/// <see cref="ICalendarFeedContributor"/> and serializes the merged result.
/// Calls services only (never repositories).
/// </summary>
public interface IICalFeedService : IOrchestrator
{
    /// <summary>
    /// The merged, Start-ordered calendar items for a user. No token check —
    /// callers (the admin widget) are already authorized server-side.
    /// Unknown user simply yields an empty list.
    /// </summary>
    Task<IReadOnlyList<CalendarFeedItem>> GetFeedItemsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Validates <paramref name="token"/> against the user's stored
    /// <c>ICalToken</c> and returns the serialized VCALENDAR, or null when the
    /// user is missing/merged or the token doesn't match (the controller maps
    /// null to 404 — no oracle distinguishing unknown user from wrong token).
    /// </summary>
    Task<string?> GetFeedIcsAsync(Guid userId, Guid token, CancellationToken ct = default);
}
