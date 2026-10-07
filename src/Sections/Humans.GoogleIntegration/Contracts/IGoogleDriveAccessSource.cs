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
/// <c>IUserServiceRead.GetUserInfosAsync</c> in bulk per claimed resource and applies user-state filtering (suspended, missing/rejected
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
/// Teams and Workgroups use this same fan-out. Teams unions its linked resources'
/// direct and active-child memberships, resolving shared-file permissions to the
/// highest level for each user. The connector never derives access from team membership.
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
