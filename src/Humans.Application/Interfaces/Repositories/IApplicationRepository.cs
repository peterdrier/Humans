using NodaTime;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Governance aggregate (<c>applications</c>,
/// <c>application_state_histories</c>, <c>board_votes</c>). The only non-test
/// file that may touch those DbSets.
/// </summary>
/// <remarks>
/// Returns entities with aggregate-local navigation collections
/// (<c>StateHistory</c>, <c>BoardVotes</c>) eagerly loaded when appropriate,
/// but never cross-domain navs — those are FK-only after the migration.
/// See <c>docs/architecture/design-rules.md</c> §3 for the canonical shape.
/// </remarks>
public interface IApplicationRepository
{
    /// <summary>
    /// Loads a single application by id, including its aggregate-local
    /// <c>StateHistory</c> and <c>BoardVotes</c> collections.
    /// </summary>
    Task<MemberApplication?> GetByIdAsync(Guid applicationId, CancellationToken ct = default);

    /// <summary>
    /// Returns every application for a user, ordered by <c>SubmittedAt</c>
    /// descending. Aggregate-local <c>StateHistory</c> is included.
    /// </summary>
    Task<IReadOnlyList<MemberApplication>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// True if the user has a pending (Submitted) application. Used by
    /// <c>SubmitAsync</c> to enforce the "one pending application per user"
    /// invariant.
    /// </summary>
    Task<bool> AnySubmittedForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Count applications in a given status. Used by the admin dashboard
    /// and the Board daily digest.
    /// </summary>
    Task<int> CountByStatusAsync(ApplicationStatus status, CancellationToken ct = default);

    /// <summary>
    /// Paginated filtered list of applications for the admin
    /// <c>Applications.cshtml</c> view. Default <paramref name="status"/>
    /// (null) maps to <see cref="ApplicationStatus.Submitted"/>, preserving
    /// pre-migration behavior.
    /// </summary>
    Task<(IReadOnlyList<MemberApplication> Items, int TotalCount)> GetFilteredAsync(
        ApplicationStatus? status,
        MembershipTier? tier,
        int page,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// Persists a new application.
    /// </summary>
    Task AddAsync(MemberApplication application, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to an existing application (e.g. Withdraw).
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
    /// vote on this application. Used by the caching decorator to
    /// invalidate per-voter voting badges after a successful finalize.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetVoterIdsForApplicationAsync(Guid applicationId, CancellationToken ct = default);

    /// <summary>
    /// Returns the user ids from <paramref name="userIds"/> that have a pending
    /// (Submitted) application. Read-only.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetUserIdsWithSubmittedAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Returns the single Submitted application for the given user, or null
    /// if none. Read-only.
    /// </summary>
    Task<MemberApplication?> GetSubmittedForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct Approved-status membership tiers for a user.
    /// Read-only.
    /// </summary>
    Task<IReadOnlyList<MembershipTier>> GetApprovedTiersForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns every Submitted application, including aggregate-local
    /// <c>BoardVotes</c>, ordered by tier then <c>SubmittedAt</c>. Read-only.
    /// </summary>
    Task<IReadOnlyList<MemberApplication>> GetAllSubmittedWithVotesAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Returns true if the application has any board votes. Used for
    /// pre-finalize gating.
    /// </summary>
    Task<bool> HasBoardVotesAsync(Guid applicationId, CancellationToken ct = default);

    /// <summary>
    /// Returns the existing board vote for (applicationId, boardMemberUserId),
    /// or null if none. Read-only for callers that will mutate via
    /// <see cref="UpsertBoardVoteAsync"/>.
    /// </summary>
    Task<BoardVote?> GetBoardVoteAsync(
        Guid applicationId, Guid boardMemberUserId, CancellationToken ct = default);

    /// <summary>
    /// Upserts a board vote: if a vote row exists for the
    /// (applicationId, boardMemberUserId) pair, updates its
    /// <see cref="BoardVote.Vote"/>/<see cref="BoardVote.Note"/>/
    /// <see cref="BoardVote.UpdatedAt"/>; otherwise inserts a new row with the
    /// provided values. Persists atomically.
    /// </summary>
    Task UpsertBoardVoteAsync(
        Guid applicationId,
        Guid boardMemberUserId,
        VoteChoice vote,
        string? note,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the number of Submitted applications that the given board
    /// member has not yet voted on.
    /// </summary>
    Task<int> GetUnvotedCountForBoardMemberAsync(
        Guid boardMemberUserId, CancellationToken ct = default);

    /// <summary>
    /// Returns aggregate counts used by the admin dashboard's tier
    /// application block. All counts exclude <see cref="ApplicationStatus.Withdrawn"/>.
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
    /// Returns Approved applications that have been resolved within
    /// the half-open window <c>[windowStart, windowEnd)</c>, ordered by
    /// <see cref="MembershipTier"/> then <c>ResolvedAt</c>. Used by the
    /// Board daily digest.
    /// </summary>
    Task<IReadOnlyList<MemberApplication>> GetApprovedInWindowAsync(
        Instant windowStart, Instant windowEnd, CancellationToken ct = default);

    /// <summary>
    /// Returns every Submitted application id. Used by the Board daily
    /// digest to compute per-member unvoted counts without re-loading the
    /// full application set.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetSubmittedApplicationIdsAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Returns the number of applications from <paramref name="applicationIds"/>
    /// that the given board member has NOT yet voted on. Used by the Board
    /// daily digest to render the per-member queue size.
    /// </summary>
    Task<int> GetUnvotedCountForBoardMemberAmongApplicationsAsync(
        Guid boardMemberUserId,
        IReadOnlyCollection<Guid> applicationIds,
        CancellationToken ct = default);

    /// <summary>
    /// Stamps <c>Application.RenewalReminderSentAt</c> to
    /// <paramref name="sentAt"/>. No-op if the application does not exist.
    /// </summary>
    Task MarkRenewalReminderSentAsync(
        Guid applicationId, Instant sentAt, CancellationToken ct = default);
}

/// <summary>
/// Aggregate counts for the admin dashboard's tier application block.
/// </summary>
public record ApplicationAdminStats(
    int Total,
    int Approved,
    int Rejected,
    int ColaboradorApplied,
    int AsociadoApplied);
