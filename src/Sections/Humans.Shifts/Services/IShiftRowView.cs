using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Services;

/// <summary>
/// The Shifts section's own view surface: the cached, entity-bearing row
/// bundles behind <see cref="Humans.Shifts.Contracts.IShiftView"/>.
/// </summary>
/// <remarks>
/// Issue #720 introduced one interface carrying both the per-user and the
/// per-rota bundle. Only the per-user one has consumers outside the section,
/// and they read a handful of scalars off it rather than the EF rows — so the
/// boundary keeps <c>IShiftView</c> with a flat
/// <see cref="Humans.Shifts.Contracts.ShiftUserSummary"/>, and this interface
/// keeps the rows for the section's own readers, which navigate
/// <c>Shift</c> → <c>Rota</c> → <c>EventSettings</c> throughout
/// (nobodies-collective/Humans#866, G5).
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
