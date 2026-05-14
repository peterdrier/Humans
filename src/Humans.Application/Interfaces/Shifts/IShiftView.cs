using Humans.Application.DTOs.Shifts;

namespace Humans.Application.Interfaces.Shifts;

/// <summary>
/// Read-only Shifts-section view surface. Returns bundled, immutable
/// projections (<see cref="ShiftUserView"/>, <see cref="ShiftRotaView"/>)
/// keyed by user / rota id.
/// </summary>
/// <remarks>
/// Synchronous. The public registration is a Singleton decorator
/// (<c>CachingShiftViewService</c>) that serves dict hits without a DB call
/// and lazily loads on miss via a Scoped inner service. Missing ids — or no
/// active event — return an empty view record, never <c>null</c>, never an
/// exception.
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
    ShiftUserView GetUser(Guid userId);

    /// <summary>
    /// Returns cached views for many users in one call, keyed by user id.
    /// Unknown users yield an empty view entry.
    /// </summary>
    IReadOnlyDictionary<Guid, ShiftUserView> GetUsers(IEnumerable<Guid> userIds);

    /// <summary>
    /// Returns the cached view for a single rota. Never <c>null</c> — empty
    /// view (with <c>Rota = null</c>) for unknown rota ids.
    /// </summary>
    ShiftRotaView GetRota(Guid rotaId);

    /// <summary>
    /// Returns cached views for many rotas in one call, keyed by rota id.
    /// Unknown rotas yield an empty view entry.
    /// </summary>
    IReadOnlyDictionary<Guid, ShiftRotaView> GetRotas(IEnumerable<Guid> rotaIds);
}
