using Humans.Domain.Entities;
using Humans.Domain.Enums;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Interfaces;

public record OnboardingResult(bool Success, string? ErrorKey = null);

public interface IOnboardingService
{
    // --- Queries ---
    Task<(List<Profile> Pending, List<Profile> Flagged, HashSet<Guid> PendingAppUserIds,
          Dictionary<Guid, (int Signed, int Required)> ConsentProgress)>
        GetReviewQueueAsync(CancellationToken ct = default);
    Task<(Profile? Profile, int ConsentCount, int RequiredConsentCount,
          MemberApplication? PendingApplication)>
        GetReviewDetailAsync(Guid userId, CancellationToken ct = default);
    Task<(List<MemberApplication> Applications, List<(Guid UserId, string DisplayName)> BoardMembers)>
        GetBoardVotingDashboardAsync(CancellationToken ct = default);
    Task<MemberApplication?> GetBoardVotingDetailAsync(Guid applicationId, CancellationToken ct = default);

    // --- Consent check mutations ---
    Task<OnboardingResult> ClearConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default);
    Task<OnboardingResult> FlagConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default);

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

    // --- Shared: consent-check pending (used by ConsentController + ProfileController) ---
    Task<bool> SetConsentCheckPendingIfEligibleAsync(Guid userId, CancellationToken ct = default);

    // --- Admin ---
    Task<DTOs.AdminDashboardData> GetAdminDashboardAsync(CancellationToken ct = default);
    Task<OnboardingResult> PurgeHumanAsync(Guid userId, CancellationToken ct = default);
}
