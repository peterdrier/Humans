using Humans.Users.Contracts;
using Humans.Base.Interfaces.Repositories;
using Humans.Governance.Contracts;
using Humans.Governance.Domain;
using NodaTime;
using MemberApplication = Humans.Governance.Domain.Application;

namespace Humans.Governance.Data;

/// <summary>
/// Repository for the Governance aggregate (<c>applications</c>,
/// <c>application_state_history</c>, <c>board_votes</c>). The only non-test
/// file that may touch those DbSets.
/// </summary>
/// <remarks>
/// Aggregate-local navs only (<c>StateHistory</c>, <c>BoardVotes</c>), eagerly
/// loaded where the caller needs them; never cross-domain navs.
/// </remarks>
internal interface IApplicationRepository : IRepository
{
    Task<MemberApplication?> GetByIdAsync(Guid applicationId, CancellationToken ct = default);

    /// <summary>
    /// Ordered by <c>SubmittedAt</c> descending, with <c>StateHistory</c>.
    /// </summary>
    Task<IReadOnlyList<MemberApplication>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Backs the "one pending application per user" invariant in <c>SubmitAsync</c>.
    /// </summary>
    Task<bool> AnySubmittedForUserAsync(Guid userId, CancellationToken ct = default);

    Task<int> CountByStatusAsync(ApplicationStatus status, CancellationToken ct = default);

    /// <summary>
    /// Paginated filtered list of applications for the admin
    /// <c>Views/Governance/Applications/Admin.cshtml</c> view. Default <paramref name="status"/>
    /// (null) maps to <see cref="ApplicationStatus.Submitted"/>, preserving
    /// pre-migration behavior.
    /// </summary>
    Task<(IReadOnlyList<MemberApplication> Items, int TotalCount)> GetFilteredAsync(
        ApplicationStatus? status,
        MembershipTier? tier,
        int page,
        int pageSize,
        CancellationToken ct = default);

    Task AddAsync(MemberApplication application, CancellationToken ct = default);

    /// <summary>
    /// Does NOT delete BoardVotes — see <see cref="FinalizeAsync"/> for the
    /// approve/reject transactional commit.
    /// </summary>
    Task UpdateAsync(MemberApplication application, CancellationToken ct = default);

    /// <summary>
    /// Atomic finalize for approve/reject: persists the already-mutated
    /// <paramref name="application"/> (state, history row, term expiry,
    /// decision note) AND bulk-deletes every <c>BoardVote</c> row for this
    /// application, all in one <c>SaveChangesAsync</c>. Call
    /// <see cref="GetVoterIdsForApplicationAsync"/> BEFORE this if the
    /// caller needs voter ids for post-write cache invalidation.
    /// </summary>
    Task FinalizeAsync(MemberApplication application, CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct user ids of every Board member who has cast a
    /// vote on this application, so the per-voter voting badges can be
    /// invalidated after a successful finalize.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetVoterIdsForApplicationAsync(Guid applicationId, CancellationToken ct = default);

    Task<IReadOnlySet<Guid>> GetUserIdsWithSubmittedAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task<MemberApplication?> GetSubmittedForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Includes <c>BoardVotes</c>; ordered by tier then <c>SubmittedAt</c>.
    /// </summary>
    Task<IReadOnlyList<MemberApplication>> GetAllSubmittedWithVotesAsync(
        CancellationToken ct = default);

    /// <summary>
    /// One row per (applicationId, boardMemberUserId): an existing vote is
    /// overwritten, never appended to. Persists atomically.
    /// </summary>
    Task UpsertBoardVoteAsync(
        Guid applicationId,
        Guid boardMemberUserId,
        VoteChoice vote,
        string? note,
        Instant now,
        CancellationToken ct = default);

    Task<int> GetUnvotedCountForBoardMemberAsync(
        Guid boardMemberUserId, CancellationToken ct = default);

    /// <summary>
    /// All counts exclude <see cref="ApplicationStatus.Withdrawn"/>.
    /// </summary>
    Task<ApplicationAdminStats> GetAdminStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns every Approved application whose <c>TermExpiresAt</c> falls
    /// between <paramref name="today"/> (inclusive) and
    /// <paramref name="reminderThreshold"/> (inclusive) and whose
    /// <c>RenewalReminderSentAt</c> is still null. Read-only.
    /// </summary>
    Task<IReadOnlyList<MemberApplication>> GetExpiringApplicationsNeedingReminderAsync(
        LocalDate today, LocalDate reminderThreshold, CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct <c>(UserId, MembershipTier)</c> pairs across
    /// every Submitted application. Used by the term renewal reminder
    /// to suppress renewals for users who already have a pending application.
    /// </summary>
    Task<IReadOnlySet<(Guid UserId, MembershipTier Tier)>> GetPendingApplicationUserTiersAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Stamps <c>Application.RenewalReminderSentAt</c> to
    /// <paramref name="sentAt"/>. No-op if the application does not exist.
    /// </summary>
    Task MarkRenewalReminderSentAsync(
        Guid applicationId, Instant sentAt, CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct user ids of every Approved application for
    /// <paramref name="tier"/> whose term is still active on
    /// <paramref name="today"/> (<c>TermExpiresAt</c> is null or on/after
    /// <paramref name="today"/>).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveApprovedTierUserIdsAsync(
        MembershipTier tier, LocalDate today, CancellationToken ct = default);

    Task<bool> HasActiveApprovedTierAsync(
        Guid userId, MembershipTier tier, LocalDate today, CancellationToken ct = default);

    /// <summary>
    /// Active-approved non-Volunteer tiers other than <paramref name="excludeTier"/>,
    /// one arbitrary row per user. Drives the system team sync's downgrade.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, MembershipTier>> GetOtherActiveTierAssignmentsAsync(
        MembershipTier excludeTier, LocalDate today, CancellationToken ct = default);

    /// <summary>
    /// GDPR Art. 17: clears the applicant's own free text (motivation, additional
    /// info, contribution, role understanding) and the reviewer prose attached to
    /// their applications, leaving the tier/status/date skeleton the association
    /// must keep. Also clears notes on votes the user cast as a Board member.
    /// </summary>
    Task<int> ScrubFreeTextForUserAsync(Guid userId, Instant updatedAt, CancellationToken ct = default);

    /// <summary>
    /// Plain re-FK: no conflict rule, no dedup — every row is preserved,
    /// because a person may hold applications on both sides. Returns the count
    /// attributed to <paramref name="targetUserId"/> after the move.
    /// </summary>
    Task<int> ReassignApplicationsToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Instant updatedAt,
        CancellationToken ct = default);
}
