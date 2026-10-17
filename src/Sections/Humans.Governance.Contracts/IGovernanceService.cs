using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Governance.Contracts;

/// <summary>
/// Cross-section read surface for the Governance section: tier-application
/// counts, stats, snapshots, and active-tier eligibility lookups. External
/// sections inject this interface; the submit-path writes they make live on
/// <see cref="IGovernanceWrite"/>. Returns are scalars, enums,
/// Guid sets, and section read DTOs — no EF entities. See
/// <c>memory/architecture/section-public-contract.md</c>.
/// </summary>
public interface IGovernanceService : IApplicationService
{
    /// <summary>
    /// Returns a user's own applications, ordered by <c>SubmittedAt</c> desc.
    /// Callers only read scalar fields (Status, MembershipTier, SubmittedAt,
    /// ResolvedAt) so the entity shape is preserved.
    /// </summary>
    Task<IReadOnlyList<UserApplicationSnapshot>> GetUserApplicationsAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the set of user ids (from the given input) that have a pending
    /// Submitted application. Used by the onboarding review queue to show a
    /// "has pending application" badge for each profile without joining across
    /// the Governance boundary.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetUserIdsWithPendingApplicationAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task<SubmittedApplicationSnapshot?> GetSubmittedApplicationForUserAsync(
        Guid userId, CancellationToken ct = default);

    Task<int> GetUnvotedApplicationCountAsync(
        Guid boardMemberUserId, CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct user ids whose Approved application for
    /// <paramref name="tier"/> still has an active term on
    /// <paramref name="today"/> (<c>TermExpiresAt</c> is null or on/after
    /// <paramref name="today"/>). Used by <c>SystemTeamSyncJob.SyncTierTeamAsync</c>
    /// so the job never reads <c>applications</c> directly (design-rules §2c).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveApprovedTierUserIdsAsync(
        MembershipTier tier, LocalDate today, CancellationToken ct = default);

    /// <summary>
    /// Does <paramref name="userId"/> have an Approved application for
    /// <paramref name="tier"/> whose term is still active on
    /// <paramref name="today"/>? Used by
    /// <c>SystemTeamSyncJob.SyncTierMembershipForUserAsync</c>.
    /// </summary>
    Task<bool> HasActiveApprovedTierAsync(
        Guid userId, MembershipTier tier, LocalDate today, CancellationToken ct = default);

    /// <summary>
    /// Returns the per-user "other active tier" map: for each user with an
    /// Approved application for a non-<paramref name="excludeTier"/>
    /// non-Volunteer tier that is still active on <paramref name="today"/>,
    /// returns the tier. Each user maps to a single entry (the first Approved
    /// row found); callers use this to decide whether a tier-downgrade
    /// should land at Volunteer or at the alternate tier. Used by
    /// <c>SystemTeamSyncJob.SyncTierTeamAsync</c>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, MembershipTier>> GetOtherActiveTierAssignmentsAsync(
        MembershipTier excludeTier, LocalDate today, CancellationToken ct = default);
}

/// <summary>
/// Aggregate counts for the admin dashboard's tier application block.
/// </summary>
public sealed record ApplicationAdminStats(
    int Total,
    int Approved,
    int Rejected,
    int ColaboradorApplied,
    int AsociadoApplied);
