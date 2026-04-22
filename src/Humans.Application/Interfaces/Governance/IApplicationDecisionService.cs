using Humans.Application.DTOs;
using Humans.Application.DTOs.Governance;
using Humans.Domain.Enums;
using NodaTime;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Interfaces.Governance;

/// <summary>
/// Single code path for tier application lifecycle: submit, withdraw, approve, reject.
/// Handles state transitions, term expiry, profile tier update, audit log,
/// GDPR vote cleanup, team sync, and notification email.
/// </summary>
/// <remarks>
/// After the Governance repo/store/decorator migration, three read methods
/// return stitched DTOs instead of <see cref="MemberApplication"/> entities:
/// detail and filtered-list shapes now carry user/reviewer display info
/// resolved via <c>IUserService</c>, because the entity no longer carries
/// cross-domain navigation properties (design-rules §6).
/// </remarks>
public interface IApplicationDecisionService
{
    Task<ApplicationDecisionResult> ApproveAsync(
        Guid applicationId,
        Guid reviewerUserId,
        string? notes,
        LocalDate? boardMeetingDate,
        CancellationToken cancellationToken = default);

    Task<ApplicationDecisionResult> RejectAsync(
        Guid applicationId,
        Guid reviewerUserId,
        string reason,
        LocalDate? boardMeetingDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a user's own applications, ordered by <c>SubmittedAt</c> desc.
    /// Callers only read scalar fields (Status, MembershipTier, SubmittedAt,
    /// ResolvedAt) so the entity shape is preserved.
    /// </summary>
    Task<IReadOnlyList<MemberApplication>> GetUserApplicationsAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns a user's own application detail with reviewer display name
    /// stitched from <c>IUserService</c>. Null if the application does not
    /// belong to the user or does not exist.
    /// </summary>
    Task<ApplicationUserDetailDto?> GetUserApplicationDetailAsync(
        Guid applicationId, Guid userId, CancellationToken ct = default);

    Task<ApplicationDecisionResult> SubmitAsync(
        Guid userId, MembershipTier tier, string motivation,
        string? additionalInfo, string? significantContribution, string? roleUnderstanding,
        string language, CancellationToken ct = default);

    Task<ApplicationDecisionResult> WithdrawAsync(
        Guid applicationId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Paginated admin list of applications with applicant user display
    /// fields stitched in. Defaults to <see cref="ApplicationStatus.Submitted"/>
    /// when <paramref name="statusFilter"/> is null/empty/unrecognized.
    /// </summary>
    Task<(IReadOnlyList<ApplicationAdminRowDto> Items, int TotalCount)> GetFilteredApplicationsAsync(
        string? statusFilter, string? tierFilter, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Admin detail view for a single application with user + reviewer
    /// display fields stitched from <c>IUserService</c>.
    /// </summary>
    Task<ApplicationAdminDetailDto?> GetApplicationDetailAsync(
        Guid applicationId, CancellationToken ct = default);

    /// <summary>
    /// Updates a submitted (draft) application's motivation, tier, and optional
    /// Asociado fields. Only allowed on <see cref="ApplicationStatus.Submitted"/>
    /// applications. Used by the profile save flow during initial setup.
    /// </summary>
    Task UpdateDraftApplicationAsync(
        Guid applicationId, MembershipTier tier, string motivation,
        string? additionalInfo, string? significantContribution, string? roleUnderstanding,
        CancellationToken ct = default);

    // ==========================================================================
    // Board voting — moved from OnboardingService (design-rules §2c:
    // applications/board_votes are Governance tables).
    // ==========================================================================

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
    Task<MemberApplication?> GetSubmittedApplicationForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct Approved-status tier values for a user. Used by
    /// the consent-check flow to decide which system-team syncs to run after
    /// a clear-consent-check action.
    /// </summary>
    Task<IReadOnlyList<MembershipTier>> GetApprovedTiersForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the Board voting dashboard: every Submitted application with
    /// its aggregate-local <c>BoardVotes</c>, plus the list of current Board
    /// members. Applicant + Board member display fields are stitched via
    /// <c>IUserService</c>.
    /// </summary>
    Task<BoardVotingDashboardData> GetBoardVotingDashboardAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Returns the Board voting detail view for a single application with
    /// voter display names stitched. Returns null if the application does
    /// not exist.
    /// </summary>
    Task<BoardVotingDetailData?> GetBoardVotingDetailAsync(
        Guid applicationId, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the application has any board votes. Used to gate
    /// finalization (no votes ⇒ no finalize).
    /// </summary>
    Task<bool> HasBoardVotesAsync(Guid applicationId, CancellationToken ct = default);

    /// <summary>
    /// Records a board vote (upsert by (applicationId, boardMemberUserId)).
    /// Error keys: <c>NotFound</c>, <c>NotSubmitted</c>.
    /// </summary>
    Task<ApplicationDecisionResult> CastBoardVoteAsync(
        Guid applicationId,
        Guid boardMemberUserId,
        VoteChoice vote,
        string? note,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the number of Submitted applications that the given board
    /// member has not yet voted on. Used by the per-board-member voting badge.
    /// </summary>
    Task<int> GetUnvotedApplicationCountAsync(
        Guid boardMemberUserId, CancellationToken ct = default);

    /// <summary>
    /// Returns the aggregate statistics for the admin dashboard's tier
    /// application block. Counts exclude <see cref="ApplicationStatus.Withdrawn"/>.
    /// </summary>
    Task<Repositories.ApplicationAdminStats> GetAdminStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the count of pending (Submitted) applications. Used by the
    /// admin dashboard.
    /// </summary>
    Task<int> GetPendingApplicationCountAsync(CancellationToken ct = default);
}

public record ApplicationDecisionResult(bool Success, string? ErrorKey = null, Guid? ApplicationId = null);
