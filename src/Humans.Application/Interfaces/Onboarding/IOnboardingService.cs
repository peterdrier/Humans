using Humans.Application.Interfaces;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Onboarding;

public record OnboardingResult(bool Success, string? ErrorKey = null);
public record BulkOnboardingResult(int ApprovedCount);

public interface IOnboardingService : IApplicationService, IOnboardingEligibilityQuery
{
    // --- Queries ---
    Task<DTOs.ReviewQueueData> GetReviewQueueAsync(CancellationToken ct = default);
    Task<DTOs.ReviewDetailData> GetReviewDetailAsync(Guid userId, CancellationToken ct = default);

    // --- Consent check mutations ---
    Task<OnboardingResult> ClearConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default);
    Task<BulkOnboardingResult> BulkClearConsentChecksAsync(
        IReadOnlyCollection<Guid> userIds, Guid reviewerId, CancellationToken ct = default);
    Task<OnboardingResult> FlagConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default);

    // --- Signup reject (consolidates OnboardingReview + Admin paths, FIXES deprovision bug) ---
    Task<OnboardingResult> RejectSignupAsync(
        Guid userId, Guid reviewerId, string? reason, CancellationToken ct = default);

    // --- Volunteer approval (FIXES missing cache eviction) ---
    Task<OnboardingResult> ApproveVolunteerAsync(
        Guid userId, Guid adminId, CancellationToken ct = default);
}
