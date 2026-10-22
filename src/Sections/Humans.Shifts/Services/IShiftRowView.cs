using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Services;

/// <summary>
/// Keeps the EF rows for the section's own readers, which navigate Shift, Rota and EventSettings.
/// <see cref="Humans.Shifts.Contracts.IShiftView"/> projects flat summaries off the same caches.
/// </summary>
internal interface IShiftRowView
{
    /// <summary>Empty, never null, for an unknown user or no active event.</summary>
    ValueTask<ShiftUserView> GetUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Unknown users yield an empty entry.</summary>
    ValueTask<IReadOnlyDictionary<Guid, ShiftUserView>> GetUsersAsync(
        IEnumerable<Guid> userIds, CancellationToken ct = default);

    /// <summary>Empty, with a null Rota, for an unknown id.</summary>
    ValueTask<ShiftRotaView> GetRotaAsync(Guid rotaId, CancellationToken ct = default);

    /// <summary>Unknown rotas yield an empty entry.</summary>
    ValueTask<IReadOnlyDictionary<Guid, ShiftRotaView>> GetRotasAsync(
        IEnumerable<Guid> rotaIds, CancellationToken ct = default);
}
