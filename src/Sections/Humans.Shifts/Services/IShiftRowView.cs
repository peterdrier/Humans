using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Services;

/// <summary>
/// The Shifts section's own view surface: the cached, entity-bearing row
/// bundles behind <see cref="Humans.Shifts.Contracts.IShiftView"/>.
/// </summary>
/// <remarks>
/// Only the per-user bundle has consumers outside the section, and they read
/// scalars — so <c>IShiftView</c> exposes the flat
/// <see cref="Humans.Shifts.Contracts.ShiftUserSummary"/> and this interface
/// keeps the EF rows for the section's own readers, which navigate
/// <c>Shift</c> → <c>Rota</c> → <c>EventSettings</c> throughout.
///
/// <para>
/// Same implementation and the same two caches serve both: the section's
/// singleton decorator implements this interface and projects
/// <c>IShiftView</c> off the cached rows.
/// </para>
/// </remarks>
internal interface IShiftRowView
{
    /// <summary>
    /// Returns the cached row bundle for a single user. Never <c>null</c> —
    /// empty bundle for unknown users / no active event.
    /// </summary>
    ValueTask<ShiftUserView> GetUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns cached row bundles for many users in one call, keyed by user id.
    /// Unknown users yield an empty bundle entry.
    /// </summary>
    ValueTask<IReadOnlyDictionary<Guid, ShiftUserView>> GetUsersAsync(
        IEnumerable<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Returns the cached row bundle for a single rota. Never <c>null</c> —
    /// empty bundle (with <c>Rota = null</c>) for unknown rota ids.
    /// </summary>
    ValueTask<ShiftRotaView> GetRotaAsync(Guid rotaId, CancellationToken ct = default);

    /// <summary>
    /// Returns cached row bundles for many rotas in one call, keyed by rota id.
    /// Unknown rotas yield an empty bundle entry.
    /// </summary>
    ValueTask<IReadOnlyDictionary<Guid, ShiftRotaView>> GetRotasAsync(
        IEnumerable<Guid> rotaIds, CancellationToken ct = default);
}
