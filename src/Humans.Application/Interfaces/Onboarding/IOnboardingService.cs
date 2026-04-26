using Humans.Application.DTOs.Governance;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Interfaces.Onboarding;

public record OnboardingResult(bool Success, string? ErrorKey = null);

public interface IOnboardingService : IOnboardingEligibilityQuery
{
    // --- Queries ---
    Task<DTOs.ReviewQueueData> GetReviewQueueAsync(CancellationToken ct = default);
    Task<DTOs.ReviewDetailData> GetReviewDetailAsync(Guid userId, CancellationToken ct = default);
    Task<DTOs.BoardVotingDashboardData> GetBoardVotingDashboardAsync(CancellationToken ct = default);
    Task<BoardVotingDetailData?> GetBoardVotingDetailAsync(Guid applicationId, CancellationToken ct = default);

    // --- Consent check mutations ---
    Task<OnboardingResult> ClearConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default);
    Task<OnboardingResult> FlagConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default);

    /// <summary>
    /// Auto-clear a pending consent check on behalf of the system (no human reviewer).
    /// Used by <c>AutoConsentCheckJob</c> after the LLM assistant gives a clean verdict.
    /// Same semantics as <see cref="ClearConsentCheckAsync"/> (IsApproved=true, cache
    /// eviction, Volunteers team sync) but audited as
    /// <see cref="Humans.Domain.Enums.AuditAction.ConsentCheckAutoCleared"/> with the
    /// job name as the actor, and no ConsentCheckedByUserId set.
    /// </summary>
    Task<OnboardingResult> AutoClearConsentCheckAsync(
        Guid userId, string reason, string modelId, CancellationToken ct = default);

    // --- Board vote ---
    Task<bool> HasBoardVotesAsync(Guid applicationId, CancellationToken ct = default);
    Task<OnboardingResult> CastBoardVoteAsync(
        Guid applicationId, Guid boardMemberUserId, VoteChoice vote, string? note, CancellationToken ct = default);

    // --- Signup reject (consolidates OnboardingReview + Admin paths, FIXES deprovision bug) ---
    Task<OnboardingResult> RejectSignupAsync(
        Guid userId, Guid reviewerId, string? reason, CancellationToken ct = default);

    // --- Volunteer approval (FIXES missing cache eviction) ---
    Task<OnboardingResult> ApproveVolunteerAsync(
        Guid userId, Guid adminId, CancellationToken ct = default);

    // --- Suspend / Unsuspend ---
    Task<OnboardingResult> SuspendAsync(
        Guid userId, Guid adminId, string? notes, CancellationToken ct = default);
    Task<OnboardingResult> UnsuspendAsync(
        Guid userId, Guid adminId, CancellationToken ct = default);

    /// <summary>
    /// UserIds of profiles currently sitting in the Consent Check = Pending bucket
    /// (not yet cleared, not flagged, not rejected). Used by AutoConsentCheckJob.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetPendingConsentCheckUserIdsAsync(CancellationToken ct = default);

    // --- Badge counts ---
    /// <summary>
    /// Gets the count of profiles pending consent review (not yet approved, not rejected).
    /// </summary>
    Task<int> GetPendingReviewCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the count of submitted applications that the given board member has not yet voted on.
    /// </summary>
    Task<int> GetUnvotedApplicationCountAsync(Guid boardMemberUserId, CancellationToken ct = default);

    // --- Admin ---
    Task<DTOs.AdminDashboardData> GetAdminDashboardAsync(CancellationToken ct = default);
    Task<OnboardingResult> PurgeHumanAsync(Guid userId, CancellationToken ct = default);
}
