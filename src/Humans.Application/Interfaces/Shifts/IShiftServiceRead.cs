namespace Humans.Application.Interfaces.Shifts;

/// <summary>
/// Cross-section read surface for the Shifts section. External sections inject
/// this interface to ask "how many confirmed shift signups does each user have
/// for a given team (or rota) in the active event". Returns only
/// <see cref="Guid"/>→<see cref="int"/> count dictionaries and the
/// <see cref="RotaTargetInfo"/> projection — no EF entities. Implemented by the
/// Shifts management service. See
/// <c>memory/architecture/section-read-write-split.md</c>.
/// </summary>
public interface IShiftServiceRead
{
    /// <summary>
    /// Confirmed-signup counts keyed by user id, across every rota the team owns
    /// in the active event. Users with no confirmed signups are absent. Returns
    /// an empty dictionary when there is no active event.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetConfirmedSignupCountsByUserForTeamAsync(
        Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Confirmed-signup counts keyed by user id, scoped to a single rota. Users
    /// with no confirmed signups are absent.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetConfirmedSignupCountsByUserForRotaAsync(
        Guid rotaId, CancellationToken ct = default);

    /// <summary>
    /// Rota target info (rota id/name + owning team id/slug), or null if the
    /// rota does not exist.
    /// </summary>
    Task<RotaTargetInfo?> GetRotaTargetInfoAsync(Guid rotaId, CancellationToken ct = default);
}
