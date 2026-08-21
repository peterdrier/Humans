using Humans.Base.Interfaces;

namespace Humans.Shifts.Contracts;

/// <summary>
/// Read-only cross-section supplier for the event-cycle ("burn") settings
/// (Nowhere 2026, etc.). Lets sections outside Shifts (Events, Camps,
/// Tickets, Notifications, ...) read calendar + early-entry metadata as a
/// <see cref="BurnSettingsInfo"/> DTO without touching
/// <c>DbContext.EventSettings</c> directly (design-rules §2c,
/// <c>memory/architecture/no-cross-section-ef-joins.md</c>).
///
/// <para>
/// Mutations + Shifts-internal reads (flags, caps, rotas) stay on
/// <c>IShiftManagementService</c> — the Shifts section is the single
/// writer of <c>event_settings</c>.
/// </para>
/// </summary>
public interface IBurnSettingsService : IApplicationService
{
    /// <summary>
    /// Loads the single active burn (invariant: at most one row with
    /// <c>IsActive == true</c>). Returns null when no active burn is
    /// configured.
    /// </summary>
    Task<BurnSettingsInfo?> GetActiveAsync(CancellationToken ct = default);

    /// <summary>
    /// Loads a burn by id. Used both by the Events section (to fetch the
    /// burn linked by <c>EventGuideSettings.EventSettingsId</c>) and for
    /// historical-cycle reads — e.g. next year, when copying setup from a
    /// previous cycle.
    /// </summary>
    Task<BurnSettingsInfo?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Every burn, oldest cycle first. Added for the Settings section's carry
    /// screen, which copies these rows into <c>settings_event</c> one cycle at a
    /// time and needs to see all of them, not just the active one
    /// (nobodies-collective/Humans#1104). Retires with the carry screen.
    /// </summary>
    Task<IReadOnlyList<BurnSettingsInfo>> GetAllAsync(CancellationToken ct = default);
}
