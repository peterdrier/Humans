using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Calendar.Contracts;

/// <summary>
/// Implemented by sections that own dated things a member is part of. A contributor reads only its own
/// section's tables.
/// </summary>
public interface ICalendarFeedContributor : IFanout
{
    /// <summary>Absolute instants: each section resolves its own wall-clock times. Must be read-only.</summary>
    Task<IReadOnlyList<CalendarFeedItem>> GetCalendarItemsForUserAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Items everyone may see on the community calendar that overlap [<paramref name="from"/>, <paramref name="to"/>].
    /// Must be read-only; empty when there is nothing public.
    /// </summary>
    Task<IReadOnlyList<CalendarFeedItem>> GetPublicItemsForWindowAsync(Instant from, Instant to, CancellationToken ct);
}
