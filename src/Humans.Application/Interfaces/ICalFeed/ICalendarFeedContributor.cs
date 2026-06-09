namespace Humans.Application.Interfaces.ICalFeed;

/// <summary>
/// Contributes calendar items to a user's personal iCal feed.
///
/// <para>
/// Sections that own user-scheduled things (shift signups, favourited guide
/// events, ...) implement this interface; the orchestrator
/// (IICalFeedService) fans out and assembles one VCALENDAR
/// without any cross-section database reads. A contributor reads only from
/// its owning section's tables — cross-section names (teams, burn settings)
/// flow through the existing IServiceRead surfaces.
/// </para>
/// </summary>
public interface ICalendarFeedContributor : IFanout
{
    /// <summary>
    /// Returns every calendar item this contributor owns for
    /// <paramref name="userId"/>, with absolute start/end instants (each
    /// section resolves its own wall-clock times through its event-settings
    /// timezone). Implementations must be read-only.
    /// </summary>
    Task<IReadOnlyList<CalendarFeedItem>> GetCalendarItemsForUserAsync(Guid userId, CancellationToken ct);
}
