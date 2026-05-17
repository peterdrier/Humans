using Humans.Application.Architecture;
using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Shifts;

/// <summary>
/// Read-only supplier API for <see cref="EventSettings"/>. Lets sections
/// outside Shifts (Events, Camps, Tickets, Notifications, ...) read
/// event-cycle metadata (name, year, gate-opening date, timezone, schedule
/// offsets) without touching <c>DbContext.EventSettings</c> directly
/// (design-rules §2c, <c>memory/architecture/no-cross-section-ef-joins.md</c>).
///
/// <para>
/// Mutations stay on <see cref="IShiftManagementService"/>
/// (<c>CreateAsync</c> / <c>UpdateAsync</c> / <c>DeleteEventAsync</c>) — the
/// Shifts section is the single writer of <c>event_settings</c>.
/// </para>
/// </summary>
[SurfaceBudget(3)]
public interface IEventSettingsService : IApplicationService
{
    /// <summary>
    /// Loads an <see cref="EventSettings"/> by primary key. Returns null if no
    /// such row exists.
    /// </summary>
    Task<EventSettings?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Loads the single active <see cref="EventSettings"/> (the invariant is
    /// at most one row with <c>IsActive == true</c>). Returns null when no
    /// active event is configured.
    /// </summary>
    Task<EventSettings?> GetActiveAsync(CancellationToken ct = default);

    /// <summary>
    /// Loads every active <see cref="EventSettings"/>. Today this is the
    /// active singleton (zero or one row); the method shape allows future
    /// multi-event support without breaking callers.
    /// </summary>
    Task<IReadOnlyList<EventSettings>> GetActiveOptionsAsync(CancellationToken ct = default);
}
