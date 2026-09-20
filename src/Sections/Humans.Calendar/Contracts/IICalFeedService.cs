using Humans.Base.Interfaces;

namespace Humans.Calendar.Contracts;

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
    /// Validates <paramref name="token"/> against the member's row in this section's
    /// <c>calendar_feed_tokens</c> and returns the serialized VCALENDAR, or null when
    /// the user is missing/merged or the token doesn't match (the controller maps null
    /// to 404 — no oracle distinguishing unknown user from wrong token).
    /// </summary>
    Task<string?> GetFeedIcsAsync(Guid userId, Guid token, CancellationToken ct = default);

    /// <summary>
    /// Whether the member has ever opened the feed card and minted a token. The admin
    /// widget's one read, and the only part of the token's lifecycle that leaves the
    /// section: minting and rotation stay internal, behind the member's own page.
    /// Never yields the token itself.
    /// </summary>
    Task<bool> HasFeedAsync(Guid userId, CancellationToken ct = default);
}
