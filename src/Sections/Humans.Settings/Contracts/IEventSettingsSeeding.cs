namespace Humans.Settings.Contracts;

/// <summary>
/// The dev-fixture verb <c>Humans.Development</c>'s dashboard seeder drives to mint the
/// active event cycle. Settings mints event ids and owns "which one is active"
/// (nobodies-collective/Humans#1631); the seeder calls this before seeding Shifts rotas
/// against the same id. Mirrors the precedent set by <c>IShiftSeeding</c> /
/// <c>ICampSeeding</c> / <c>ITeamSeeding</c> — a narrow, section-owned seeding seam,
/// not a widening of <see cref="ISettingsService"/>.
/// </summary>
public interface IEventSettingsSeeding
{
    /// <summary>
    /// Creates <paramref name="settings"/> and makes it the active cycle, deactivating
    /// whatever else is currently active. No audit entry — seeding fixtures have no
    /// real actor, matching the other seeding seams.
    /// </summary>
    Task CreateActiveEventAsync(EventSettingsInfo settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the event settings row by id, if it exists. Returns the number of rows
    /// deleted (0 or 1). No audit entry, matching <see cref="CreateActiveEventAsync"/> —
    /// call before or after <c>IShiftSeeding.DeleteEventAsync</c> deletes the rotas that
    /// reference this id, order doesn't matter since the two sections' tables carry no
    /// DB-level FK between them.
    /// </summary>
    Task<int> DeleteEventAsync(Guid id, CancellationToken cancellationToken = default);
}
