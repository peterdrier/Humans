using Humans.Application.Architecture;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Governance;

/// <summary>
/// Cross-section read surface for the Governance section: tier-application
/// counts, stats, snapshots, and active-tier eligibility lookups. External
/// sections inject this interface instead of the full write service
/// <see cref="IApplicationDecisionService"/>; returns are scalars, enums,
/// Guid sets, and section read DTOs — no EF entities. See
/// <c>memory/architecture/section-read-write-split.md</c>.
/// </summary>
[SurfaceBudget(10)]
public interface IApplicationServiceRead
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

    /// <summary>
    /// Returns the single Submitted application for the given user, or null
    /// if none. Used by the onboarding review detail view.
    /// </summary>
    Task<SubmittedApplicationSnapshot?> GetSubmittedApplicationForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct Approved-status tier values for a user. Used by
    /// the consent-check flow to decide which system-team syncs to run after
    /// a clear-consent-check action.
    /// </summary>
    Task<IReadOnlyList<MembershipTier>> GetApprovedTiersForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the number of Submitted applications that the given board
    /// member has not yet voted on. Used by the per-board-member voting badge.
    /// </summary>
    Task<int> GetUnvotedApplicationCountAsync(
        Guid boardMemberUserId, CancellationToken ct = default);

    /// <summary>
    /// Returns the aggregate statistics for the admin dashboard's tier
    /// application block. Counts exclude <see cref="Domain.Enums.ApplicationStatus.Withdrawn"/>.
    /// </summary>
    Task<ApplicationAdminStats> GetAdminStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the count of pending (Submitted) applications. Used by the
    /// admin dashboard.
    /// </summary>
    Task<int> GetPendingApplicationCountAsync(CancellationToken ct = default);

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
