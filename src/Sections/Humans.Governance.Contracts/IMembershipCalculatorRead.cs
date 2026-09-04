namespace Humans.Governance.Contracts;

/// <summary>
/// Cross-section read surface for the Governance membership calculator:
/// consent-completeness checks, membership/consent snapshots, required-team
/// resolution, and status-update batches. Returns are scalars, Guid
/// sets/lists, and section read DTOs — no EF entities. See
/// <c>memory/architecture/section-read-write-split.md</c>.
/// </summary>
public interface IMembershipCalculatorRead
{
    /// <summary>
    /// Checks if a user has all required consents (for the Volunteers team).
    /// </summary>
    Task<bool> HasAllRequiredConsentsAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> GetMissingConsentVersionsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets users whose membership status should be set to Inactive due to missing consent.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUsersRequiringStatusUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Filters a set of user IDs to only those who have all required consents.
    /// This is a batch operation that avoids N+1 queries.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetUsersWithAllRequiredConsentsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user has all required consents for a specific team's documents.
    /// </summary>
    Task<bool> HasAllRequiredConsentsForTeamAsync(Guid userId, Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Filters user IDs to those who have all required consents for a specific team.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetUsersWithAllRequiredConsentsForTeamAsync(
        IEnumerable<Guid> userIds, Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Returns a consolidated membership/consent snapshot for UI and policy checks.
    /// </summary>
    Task<MembershipSnapshot> GetMembershipSnapshotAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all team IDs whose required documents apply to a user.
    /// Includes current memberships plus system teams the user is eligible for
    /// (e.g., Leads team if the user is Lead of any user-created team).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetRequiredTeamIdsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Partitions a set of user IDs into 6 mutually exclusive membership categories.
    /// Every input user ID appears in exactly one bucket.
    /// Priority order: PendingDeletion > Suspended > IncompleteSignup > PendingApproval > MissingConsents/Active.
    /// </summary>
    Task<MembershipPartition> PartitionUsersAsync(
        IEnumerable<Guid> userIds, CancellationToken ct = default);
}
