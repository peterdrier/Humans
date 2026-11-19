using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Services;

/// <summary>
/// Every rota with its shifts and signups, across all events. <see cref="CachingShiftService"/> serves it from memory;
/// the keyed inner loads it from the database.
/// </summary>
internal interface IShiftRotaRows
{
    /// <summary>Null <paramref name="rotaIds"/> returns every rota; ids with no rota are left out.</summary>
    Task<IReadOnlyList<ShiftAdminRota>> GetRotaRowsAsync(
        IReadOnlyCollection<Guid>? rotaIds = null, CancellationToken ct = default);
}
