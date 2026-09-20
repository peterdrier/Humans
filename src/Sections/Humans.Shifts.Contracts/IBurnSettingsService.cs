using Humans.Base.Interfaces;

namespace Humans.Shifts.Contracts;

/// <summary>
/// Read-only supplier for the event-cycle ("burn") settings (Nowhere 2026,
/// etc.) as a <see cref="BurnSettingsInfo"/> DTO, without touching
/// <c>DbContext.EventSettings</c> directly (design-rules §2c,
/// <c>memory/architecture/no-cross-section-ef-joins.md</c>). The app-wide
/// calendar itself now lives in Settings' <c>settings_event</c>
/// (nobodies-collective/Humans#1631); every current consumer is inside this
/// section, so this interface is a candidate for retirement once nothing
/// outside Shifts needs it — kept for now since it is the section's existing
/// external-read seam.
///
/// <para>
/// Mutations + Shifts-internal reads (flags, caps, rotas) stay on
/// <c>IShiftManagementService</c> — the Shifts section is the single
/// writer of its own local <c>event_settings</c> knobs row.
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
}
