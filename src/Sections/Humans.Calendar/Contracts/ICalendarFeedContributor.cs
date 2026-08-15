using Humans.Application.Interfaces;

namespace Humans.Calendar.Contracts;

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
/// <para>
/// Lives under Calendar's <c>Contracts/</c> because a contributor fan-out inverts
/// the dependency arrow: implementers reference Calendar, Calendar references none
/// of them, so the folder is enough and no <c>.Contracts</c> leaf is needed
/// (nobodies-collective/Humans#866, G5 lane 4b-2c).
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
