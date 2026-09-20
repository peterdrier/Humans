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
}
