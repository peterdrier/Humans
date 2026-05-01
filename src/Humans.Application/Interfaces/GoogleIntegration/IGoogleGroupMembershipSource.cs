namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Implemented by every section that owns the expected membership of one or
/// more Google Groups. Each implementation declares the groups it claims
/// (keyed by group email) and the user IDs that should be members.
/// </summary>
/// <remarks>
/// <para>
/// Keys are Google Group email addresses (e.g. <c>volunteers@nobodies.team</c>).
/// Values are user IDs only — the <see cref="IGoogleGroupSync"/> orchestrator
/// hydrates them via <see cref="Users.IUserService.GetByIdsAsync"/> in a single
/// bulk call per sync pass and applies user-state filtering (suspended, missing
/// <c>GoogleEmail</c>, etc.) uniformly across all sources. Sources MUST NOT
/// call <c>IUserService</c> to satisfy this contract.
/// </para>
/// <para>
/// Collision detection is performed by the orchestrator: if two sources return
/// the same key from <see cref="GetExpectedAsync"/>, that group is skipped and
/// the collision is logged + audited. Sources do not coordinate ownership
/// among themselves.
/// </para>
/// </remarks>
public interface IGoogleGroupMembershipSource
{
    /// <summary>
    /// Returns the expected membership for every group this source claims.
    /// </summary>
    /// <param name="groupKey">
    /// When non-null, restricts the returned dictionary to at most one entry —
    /// the requested key if this source claims it, or an empty dictionary
    /// otherwise. When null, returns every group this source claims.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A dictionary keyed by Google Group email; each value is the set of
    /// user IDs that should be members of that group. Implementations may
    /// return an empty dictionary if they have nothing to claim.
    /// </returns>
    Task<Dictionary<string, Guid[]>> GetExpectedAsync(
        string? groupKey = null,
        CancellationToken ct = default);
}
