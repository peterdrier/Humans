namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Implemented by every section that owns the expected Drive access for one
/// or more Drive folders (identified by Google file id). Each implementation
/// declares the folders it claims and the per-user permission level expected
/// on each.
/// </summary>
/// <remarks>
/// <para>
/// Keys are Google Drive file ids. Values are a per-user
/// <see cref="DrivePermissionLevel"/> map — user IDs only, no emails. The
/// <see cref="IGoogleDriveSync"/> orchestrator hydrates them via
/// <c>IUserServiceRead.GetUserInfosAsync</c> in a single bulk call per sync
/// pass and applies user-state filtering (suspended, missing/rejected
/// <c>GoogleEmail</c>, etc.) uniformly across all sources. Sources MUST NOT
/// call <c>IUserService</c> to satisfy this contract.
/// </para>
/// <para>
/// Collision detection is performed by the orchestrator: if two sources
/// return the same key from <see cref="GetExpectedAccessAsync"/>, that folder
/// is skipped and the collision is logged + audited. Sources do not
/// coordinate ownership among themselves.
/// </para>
/// <para>
/// This fan-out is additive to, and independent from, the Teams-keyed
/// <c>google_resources</c> Drive path (reconciled by team membership) —
/// that path is unaffected by this contract.
/// </para>
/// </remarks>
public interface IGoogleDriveAccessSource
{
    /// <summary>
    /// Returns the expected access for every folder this source claims.
    /// </summary>
    /// <param name="folderId">
    /// When non-null, restricts the result to at most one entry — the
    /// requested folder if this source claims it, or an empty dictionary
    /// otherwise. When null, returns every folder this source claims.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A dictionary keyed by Google Drive file id; each value maps a user ID
    /// to the <see cref="DrivePermissionLevel"/> they are expected to hold on
    /// that folder. Implementations may return an empty dictionary if they
    /// have nothing to claim.
    /// </returns>
    Task<Dictionary<string, Dictionary<Guid, DrivePermissionLevel>>> GetExpectedAccessAsync(
        string? folderId = null,
        CancellationToken ct = default);
}
