using Microsoft.Extensions.Logging;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Governance;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Services.Onboarding;

// Onboarding intake-funnel orchestrator. Owns no tables; all writes go through owning-section services.
// Out of scope: suspend/unsuspend (IHumanLifecycleService), board voting (IApplicationDecisionService),
// admin dashboard (IAdminDashboardService), account deletion (future IAccountDeletionService).
public sealed class OnboardingService(
    IProfileService profileService,
    IUserService userService,
    IApplicationDecisionService applicationDecisionService,
    IEmailService emailService,
    INotificationService notificationService,
    ISystemTeamSync syncJob,
    IMembershipCalculator membershipCalculator,
    IHumansMetrics metrics,
    ILogger<OnboardingService> logger) : IOnboardingService
{
    // --- Queries: review queue ---

    public async Task<ReviewQueueData> GetReviewQueueAsync(CancellationToken ct = default)
    {
        // Review queue = not approved, not rejected, oldest first.
        var reviewable = (await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false))
            .Where(u => u.NeedsConsentReview)
            .OrderBy(u => u.Profile!.CreatedAt)
            .ToList();

        var allUserIds = reviewable.Select(u => u.Id).ToList();
        var pendingAppUserIds = await applicationDecisionService
            .GetUserIdsWithPendingApplicationAsync(allUserIds, ct);

        var consentProgress = new Dictionary<Guid, ConsentProgressInfo>();
        foreach (var userId in allUserIds)
        {
            var snapshot = await membershipCalculator.GetMembershipSnapshotAsync(userId, ct);
            consentProgress[userId] = new ConsentProgressInfo(
                snapshot.RequiredConsentCount - snapshot.PendingConsentCount,
                snapshot.RequiredConsentCount);
        }

        var flagged = reviewable
            .Where(u => u.Profile!.ConsentCheckStatus == ConsentCheckStatus.Flagged)
            .ToList();
        var pending = reviewable.Except(flagged).ToList();

        // ReviewQueueData types PendingAppUserIds as HashSet<Guid>; Governance returns IReadOnlySet.
        var pendingAppHashSet = pendingAppUserIds.ToHashSet();

        return new ReviewQueueData(pending, flagged, pendingAppHashSet, consentProgress);
    }

    public async Task<ReviewDetailData> GetReviewDetailAsync(Guid userId, CancellationToken ct = default)
    {
        var profile = (await userService.GetUserInfoAsync(userId, ct))?.Profile;

        if (profile is null)
            return new ReviewDetailData(null, 0, 0, null);

        var snapshot = await membershipCalculator.GetMembershipSnapshotAsync(userId, ct);

        var pendingApp = await applicationDecisionService
            .GetSubmittedApplicationForUserAsync(userId, ct);

        return new ReviewDetailData(
            new ReviewProfileDetail(
                profile.FirstName,
                profile.LastName,
                profile.City,
                profile.CountryCode,
                profile.MembershipTier,
                profile.ConsentCheckStatus,
                profile.ConsentCheckNotes,
                profile.CreatedAt),
            snapshot.RequiredConsentCount - snapshot.PendingConsentCount,
            snapshot.RequiredConsentCount,
            pendingApp?.Motivation);
    }

    // --- Consent-check mutations ---

    public async Task<OnboardingResult> ClearConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default)
    {
        var result = await profileService.RecordConsentCheckAsync(
            userId, reviewerId, ConsentCheckStatus.Cleared, notes, ct);
        if (!result.Success)
            return result;

        await syncJob.SyncMembershipForUserAsync(userId, SystemTeamType.Volunteers, CancellationToken.None);

        var approvedTiers = await applicationDecisionService.GetApprovedTiersForUserAsync(userId, ct);

        foreach (var tier in approvedTiers)
        {
            if (tier == MembershipTier.Colaborador)
                await syncJob.SyncMembershipForUserAsync(userId, SystemTeamType.Colaboradors, CancellationToken.None);
            else if (tier == MembershipTier.Asociado)
                await syncJob.SyncMembershipForUserAsync(userId, SystemTeamType.Asociados, CancellationToken.None);
        }

        return result;
    }

    public async Task<BulkOnboardingResult> BulkClearConsentChecksAsync(
        IReadOnlyCollection<Guid> userIds, Guid reviewerId, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new BulkOnboardingResult(0);

        var selected = userIds.ToHashSet();
        var data = await GetReviewQueueAsync(ct);
        var eligibleUserIds = data.Pending
            .Concat(data.Flagged)
            .Where(u => selected.Contains(u.Id))
            .Select(u => u.Id)
            .ToList();

        var approved = 0;
        foreach (var userId in eligibleUserIds)
        {
            var result = await ClearConsentCheckAsync(userId, reviewerId, notes: null, ct);
            if (result.Success)
            {
                approved++;
            }
            else
            {
                logger.LogWarning(
                    "BulkClearConsentChecks: skipped user {UserId}: {ErrorKey}",
                    userId,
                    result.ErrorKey);
            }
        }

        return new BulkOnboardingResult(approved);
    }

    public async Task<OnboardingResult> FlagConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default)
    {
        var result = await profileService.RecordConsentCheckAsync(
            userId, reviewerId, ConsentCheckStatus.Flagged, notes, ct);
        if (!result.Success)
            return result;

        await DeprovisionApprovalGatedSystemTeamsAsync(userId);
        return result;
    }

    // --- Signup reject / volunteer approve ---

    public async Task<OnboardingResult> RejectSignupAsync(
        Guid userId, Guid reviewerId, string? reason, CancellationToken ct = default)
    {
        var result = await profileService.RejectSignupAsync(userId, reviewerId, reason, ct);
        if (!result.Success)
            return result;

        await DeprovisionApprovalGatedSystemTeamsAsync(userId);

        var rejectUser = await userService.GetUserInfoAsync(userId, ct);

        try
        {
            await emailService.SendSignupRejectedAsync(
                rejectUser?.Email ?? string.Empty,
                rejectUser?.BurnerName ?? string.Empty,
                reason,
                rejectUser?.PreferredLanguage ?? "en");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send signup rejection email to {UserId}", userId);
        }

        try
        {
            await notificationService.SendAsync(
                NotificationSource.ProfileRejected,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                "Your signup has been reviewed",
                [userId],
                body: string.IsNullOrWhiteSpace(reason)
                    ? "Your signup could not be approved at this time."
                    : $"Your signup could not be approved: {reason}",
                actionUrl: "/Profile",
                actionLabel: "View profile",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch ProfileRejected notification for user {UserId}", userId);
        }

        return result;
    }

    public async Task<OnboardingResult> ApproveVolunteerAsync(
        Guid userId, Guid adminId, CancellationToken ct = default)
    {
        // Preserves historical "NotFound" error-key contract via ProfileService.
        var result = await profileService.ApproveVolunteerAsync(userId, adminId, ct);
        if (!result.Success)
            return result;

        await syncJob.SyncMembershipForUserAsync(userId, SystemTeamType.Volunteers, ct);

        metrics.RecordVolunteerApproved();

        try
        {
            await notificationService.SendAsync(
                NotificationSource.VolunteerApproved,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                "Welcome! You have been approved",
                [userId],
                body: "Your profile has been approved. Welcome to the community!",
                actionUrl: "/Profile",
                actionLabel: "View profile",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch VolunteerApproved notification for user {UserId}", userId);
        }

        return result;
    }

    // --- Consent-check pending threshold (peer-called by controllers after Profile/Consent writes) ---

    public async Task<bool> SetConsentCheckPendingIfEligibleAsync(
        Guid userId, CancellationToken ct = default)
    {
        var info = await userService.GetUserInfoAsync(userId, ct);
        if (info is null || !info.NeedsConsentReview || info.Profile!.ConsentCheckStatus is not null)
            return false;

        var hasAllConsents = await membershipCalculator.HasAllRequiredConsentsForTeamAsync(
            userId, SystemTeamIds.Volunteers, ct);
        if (!hasAllConsents)
            return false;

        var set = await profileService.SetConsentCheckPendingAsync(userId, ct);
        if (!set)
            return false;

        return true;
    }

    // --- Helpers ---

    private async Task DeprovisionApprovalGatedSystemTeamsAsync(Guid userId)
    {
        await syncJob.SyncMembershipForUserAsync(userId, SystemTeamType.Volunteers, CancellationToken.None);
        await syncJob.SyncMembershipForUserAsync(userId, SystemTeamType.Colaboradors, CancellationToken.None);
        await syncJob.SyncMembershipForUserAsync(userId, SystemTeamType.Asociados, CancellationToken.None);
    }
}
