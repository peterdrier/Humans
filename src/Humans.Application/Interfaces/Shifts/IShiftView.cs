using Humans.Application.DTOs.Shifts;

namespace Humans.Application.Interfaces.Shifts;

/// <summary>
/// Read-only Shifts-section view surface. Returns bundled, immutable
/// projections (<see cref="ShiftUserView"/>, <see cref="ShiftRotaView"/>)
/// keyed by user / rota id.
/// </summary>
/// <remarks>
/// Methods return <see cref="ValueTask{TResult}"/>: the public registration is
/// a Singleton decorator (<c>CachingShiftViewService</c>) that completes
/// synchronously on dict hits (no <see cref="System.Threading.Tasks.Task"/>
/// allocation, no thread hop) and falls through to an awaiting load on miss.
/// Missing ids — or no active event — return an empty view record, never
/// <c>null</c>, never an exception.
///
/// <para>
/// Issue #720. Foundation for shifts caching: existing read methods on
/// <see cref="IShiftSignupService"/> and <see cref="IShiftManagementService"/>
/// migrate to this surface in follow-up PRs.
/// </para>
/// </remarks>
public interface IShiftView
{
    /// <summary>
    /// Returns the cached view for a single user. Never <c>null</c> — empty
    /// view for unknown users / no active event.
    /// </summary>
    ValueTask<ShiftUserView> GetUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns cached views for many users in one call, keyed by user id.
    /// Unknown users yield an empty view entry.
    /// </summary>
    ValueTask<IReadOnlyDictionary<Guid, ShiftUserView>> GetUsersAsync(
        IEnumerable<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Returns the cached view for a single rota. Never <c>null</c> — empty
    /// view (with <c>Rota = null</c>) for unknown rota ids.
    /// </summary>
    ValueTask<ShiftRotaView> GetRotaAsync(Guid rotaId, CancellationToken ct = default);

    /// <summary>
    /// Returns cached views for many rotas in one call, keyed by rota id.
    /// Unknown rotas yield an empty view entry.
    /// </summary>
    ValueTask<IReadOnlyDictionary<Guid, ShiftRotaView>> GetRotasAsync(
        IEnumerable<Guid> rotaIds, CancellationToken ct = default);
}
