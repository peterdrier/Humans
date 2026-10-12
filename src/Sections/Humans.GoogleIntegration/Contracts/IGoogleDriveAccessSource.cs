namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// One section's expected access to a Drive item. User IDs cross the boundary;
/// GoogleIntegration resolves eligible users and verified Google addresses.
/// </summary>
public sealed record GoogleDriveAccessClaim(Dictionary<Guid, DrivePermissionLevel> Access)
{
    /// <summary>Whether this claim may be combined with another section's claim.</summary>
    public bool AllowSharedAccess { get; init; }

    /// <summary>Whether members' existing higher permissions satisfy this claim.</summary>
    public bool PreserveHigherPermissions { get; init; }

    /// <summary>Whether inherited permissions remain untouched by this source's policy.</summary>
    public bool PreserveInheritedPermissions { get; init; }

    /// <summary>Linked resource facts for display, sync history and reconciliation status.</summary>
    public IReadOnlyList<GoogleResourceSnapshot> Resources { get; init; } = [];

    /// <summary>Linked resources to retire after their complete access reconciliation succeeds.</summary>
    public IReadOnlySet<Guid> RetiredResourceIds { get; init; } = new HashSet<Guid>();

    /// <summary>Teams contributing access to this item, for the sync dashboard.</summary>
    public List<TeamLink> LinkedTeams { get; init; } = [];

    /// <summary>Team provenance of each expected member's access.</summary>
    public IReadOnlyDictionary<Guid, List<TeamLink>> MemberTeams { get; init; }
        = new Dictionary<Guid, List<TeamLink>>();
}

/// <summary>
/// Implemented by sections owning expected Drive access. Keys are Google file IDs;
/// claims contain user IDs, permission policy and section-owned display facts.
/// </summary>
/// <remarks>
/// GoogleIntegration hydrates users, filters ineligible accounts and reconciles permissions.
/// Multiple claims are rejected unless a contributing source explicitly allows sharing;
/// shared access is the union at each user's highest requested permission level.
/// Sources do not coordinate ownership among themselves.
/// </remarks>
public interface IGoogleDriveAccessSource
{
    /// <summary>Returns every claimed item, or only the requested item when specified.</summary>
    /// <param name="folderId">Optional Google file ID to reconcile.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Dictionary<string, GoogleDriveAccessClaim>> GetExpectedAccessAsync(
        string? folderId = null,
        CancellationToken ct = default);
}
