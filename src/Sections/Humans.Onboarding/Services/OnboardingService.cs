using System.Globalization;
using System.Resources;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Consent.Contracts;
using Humans.Governance.Contracts;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Email.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;

using Humans.Teams.Contracts;

namespace Humans.Onboarding.Services;

internal sealed class OnboardingService(
    IUserService userService,
    IUserWrite userWrite,
    IConsentWrite consentWrite,
    IEmailService emailService,
    OnboardingEmails emailMessages,
    INotificationEmitter notificationService,
    ISystemTeamSync syncJob,
    IMembershipCalculator membershipCalculator,
    IConsentService consentService,
    IHumanLifecycleService humanLifecycleService,
    IAuditLogService auditLogService,
    IAccountDeletionService accountDeletionService,
    ILogger<OnboardingService> logger) : IOnboardingService
{
    private static readonly ResourceManager NoticeResources = new(typeof(OnboardingResource));

    public async Task<OnboardingResult> RejectSignupAsync(
        Guid userId, Guid reviewerId, string? reason, CancellationToken ct = default)
    {
        // Two writes: Consent records the rejection (reason, time, reviewer), Users the state.
        var result = await consentWrite.RecordRejectionAsync(userId, reviewerId, reason, ct);
        if (!result.Success)
        {
            // Consent recorded the rejection but the Users write failed: finish it here rather than refuse.
            // Once the state is Rejected, it is refused.
            if (!string.Equals(result.ErrorKey, "AlreadyRejected", StringComparison.Ordinal)
                || (await userService.GetUserInfoAsync(userId, ct))?.State is null or UserState.Rejected)
                return result;
            var recorded = await consentService.GetConsentReviewAsync(userId, ct);
            reason = recorded.RejectionReason;
            reviewerId = recorded.RejectedByUserId ?? reviewerId;
        }
        result = await userWrite.SetRejectedAsync(userId, ct);
        if (!result.Success)
            return result;

        await auditLogService.LogAsync(
            OnboardingAuditActions.SignupRejected,
            AuditEntityTypes.Profile,
            userId,
            $"Signup rejected{(string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}")}",
            reviewerId);

        logger.LogInformation("Signup rejected for user {UserId} by {ReviewerId}", userId, reviewerId);

        await DeprovisionApprovalGatedSystemTeamsAsync(userId);

        var culture = CultureInfo.GetCultureInfo("en");
        try
        {
            var rejectUser = await userService.GetUserInfoAsync(userId, ct);
            var language = rejectUser?.PreferredLanguage;
            culture = CultureInfo.GetCultureInfo(language.IsSupportedCultureCode() ? language! : "en");
            await emailService.SendAsync(emailMessages.SignupRejected(
                userId,
                rejectUser?.Email ?? string.Empty,
                rejectUser?.BurnerName ?? string.Empty,
                reason,
                culture.Name));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send signup rejection email to {UserId}", userId);
        }

        try
        {
            var body = string.IsNullOrWhiteSpace(reason)
                ? NoticeResources.GetString("Onboarding_Notification_ProfileRejectedBody", culture)!
                : string.Format(culture,
                    NoticeResources.GetString("Onboarding_Notification_ProfileRejectedWithReason", culture)!, reason);
            await notificationService.SendAsync(
                OnboardingNotificationSources.ProfileRejected,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                NoticeResources.GetString("Onboarding_Notification_ProfileRejectedTitle", culture)!,
                [userId],
                body: body,
                actionUrl: "/Profile",
                actionLabel: NoticeResources.GetString("Onboarding_Notification_ViewProfile", culture)!,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch ProfileRejected notification for user {UserId}", userId);
        }

        return result;
    }

    public async Task ProfileSavedAsync(Guid userId, CancellationToken ct = default)
    {
        var info = await userService.GetUserInfoAsync(userId, ct);
        if (info is null) return;
        var review = await consentService.GetConsentReviewAsync(info.Id, ct);
        await SetConsentCheckPendingIfEligibleAsync(info, review, ct);

        // Finishing setup is a change of mind: before approval, saving the profile withdraws a pending
        // deletion request.
        if (!info.IsDeletionPending || review.IsApproved)
            return;

        await accountDeletionService.CancelDeletionAsync(info.Id, ct);
        logger.LogInformation(
            "Cancelled pending deletion request for user {UserId} on profile setup", info.Id);
    }

    public Task ConsentSignedAsync(Guid userId, CancellationToken ct = default) =>
        SetConsentCheckPendingIfEligibleAsync(userId, ct);

    /// <summary>Raises no notification: the queue shows in the sidebar's Review pill.</summary>
    private async Task SetConsentCheckPendingIfEligibleAsync(Guid userId, CancellationToken ct)
    {
        var info = await userService.GetUserInfoAsync(userId, ct);
        if (info is null)
            return;
        var review = await consentService.GetConsentReviewAsync(info.Id, ct);
        await SetConsentCheckPendingIfEligibleAsync(info, review, ct);
    }

    private async Task SetConsentCheckPendingIfEligibleAsync(
        UserInfo info, ConsentReviewInfo review, CancellationToken ct)
    {
        if (!info.NeedsConsentReview(review) || review.Status is not null)
            return;
        var userId = info.Id;

        var hasAllConsents = await membershipCalculator.HasAllRequiredConsentsForTeamAsync(
            userId, SystemTeamIds.Volunteers, ct);
        if (!hasAllConsents)
            return;

        var result = await consentWrite.SetConsentCheckPendingAsync(userId, ct);
        if (!result.Success)
            return;

        logger.LogInformation(
            "User {UserId} has all consents signed, consent check set to Pending", userId);
    }

    public async Task<NextConsentStepData> GetNextUnsignedConsentAsync(
        Guid userId, CancellationToken ct = default)
    {
        var rows = await consentService.GetRequiredConsentRowsForUserAsync(
            userId, SystemTeamIds.Volunteers, ct);
        var unsigned = rows.Where(r => !r.Signed).ToList();
        if (unsigned.Count == 0)
        {
            // Without this a compliant consent-suspended user loops Status → Consents → Home → Status forever:
            // the un-suspend otherwise fires only on a fresh signature.
            await humanLifecycleService.RestoreConsentSuspensionAsync(userId, ct);
            return new NextConsentStepData(NextConsentStepOutcome.NothingLeftToSign, null, 0, rows.Count);
        }

        var detail = await consentService.GetConsentReviewDetailAsync(
            unsigned[0].DocumentVersionId, userId, ct);
        if (detail is null)
        {
            logger.LogError(
                "Required consent document version {DocumentVersionId} has no reviewable detail; "
                + "user {UserId} cannot complete the onboarding consent step",
                unsigned[0].DocumentVersionId,
                userId);
            return new NextConsentStepData(NextConsentStepOutcome.DocumentUnavailable, null, 0, rows.Count);
        }

        return new NextConsentStepData(
            NextConsentStepOutcome.Document, detail, rows.Count - unsigned.Count + 1, rows.Count);
    }

    private async Task DeprovisionApprovalGatedSystemTeamsAsync(Guid userId)
    {
        await syncJob.SyncMembershipForUserAsync(userId, SystemTeamType.Volunteers, CancellationToken.None);
        await syncJob.SyncMembershipForUserAsync(userId, SystemTeamType.Colaboradors, CancellationToken.None);
        await syncJob.SyncMembershipForUserAsync(userId, SystemTeamType.Asociados, CancellationToken.None);
    }
}
