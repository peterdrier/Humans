using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Persistence boundary for the Camps section's shift-obligation tables —
/// <c>shift_obligations</c> (the standing function definitions) and
/// <c>camp_season_shift_obligations</c> (per-season required-count overrides).
/// Owned by the Camps section; touches only these two DbSets so each table
/// belongs to exactly one repository (HUM0025).
/// </summary>
/// <remarks>
/// Reads are <c>AsNoTracking</c>. Overrides are fetched by explicit
/// <c>campSeasonId</c> set (never by joining <c>camp_seasons</c> on Year) so
/// this repository never reaches into the camp-season table it does not own —
/// the application service supplies the season ids it already holds from
/// <see cref="Camps.ICampServiceRead"/>.
/// </remarks>
public interface IShiftObligationRepository : IRepository
{
    Task<IReadOnlyList<ShiftObligation>> GetAllAsync(CancellationToken ct = default);

    Task<ShiftObligation?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task AddAsync(ShiftObligation obligation, CancellationToken ct = default);

    Task UpdateAsync(ShiftObligation obligation, CancellationToken ct = default);

    /// <summary>
    /// Returns every per-season override row for the given camp-season ids.
    /// Empty set returns empty list. Never joins <c>camp_seasons</c>.
    /// </summary>
    Task<IReadOnlyList<CampSeasonShiftObligation>> GetOverridesForSeasonsAsync(
        IReadOnlyCollection<Guid> campSeasonIds, CancellationToken ct = default);

    /// <summary>
    /// Upserts (when <paramref name="requiredShiftCount"/> is non-null) or clears
    /// (when null) the per-season override for one obligation. Idempotent.
    /// </summary>
    Task SetOverrideAsync(
        Guid campSeasonId, Guid shiftObligationId, int? requiredShiftCount,
        CancellationToken ct = default);
}
