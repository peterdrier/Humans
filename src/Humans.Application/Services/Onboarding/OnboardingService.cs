using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.DTOs.Governance;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Governance;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Services.Onboarding;

/// <summary>
/// Onboarding orchestrator. Owns no tables — coordinates
/// <see cref="IProfileService"/> (profile mutations), <see cref="IUserService"/>
/// (user purge + admin dashboard aggregates), <see cref="IApplicationDecisionService"/>
/// (tier application + board voting reads, board vote recording),
/// <see cref="IRoleAssignmentService"/> (Board member resolution), and
/// <see cref="ISystemTeamSync"/> (system team membership sync). All reads
/// and writes flow through the owning-section service interfaces, never
/// through DbContext. Cache invalidation is owned by the target services'
/// decorators/invalidators — this orchestrator never touches caches directly.
/// </summary>
public sealed class OnboardingService : IOnboardingService
{
    private readonly IProfileService _profileService;
    private readonly IUserService _userService;
    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly IEmailService _emailService;
    private readonly INotificationService _notificationService;
    private readonly INotificationInboxService _notificationInboxService;
    private readonly ISystemTeamSync _syncJob;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IHumansMetrics _metrics;
    private readonly ILogger<OnboardingService> _logger;

    public OnboardingService(
        IProfileService profileService,
        IUserService userService,
        IApplicationDecisionService applicationDecisionService,
        IEmailService emailService,
        INotificationService notificationService,
        INotificationInboxService notificationInboxService,
        ISystemTeamSync syncJob,
        IMembershipCalculator membershipCalculator,
        IHumansMetrics metrics,
        ILogger<OnboardingService> logger)
    {
        _profileService = profileService;
        _userService = userService;
        _applicationDecisionService = applicationDecisionService;
        _emailService = emailService;
        _notificationService = notificationService;
        _notificationInboxService = notificationInboxService;
        _syncJob = syncJob;
        _membershipCalculator = membershipCalculator;
        _metrics = metrics;
        _logger = logger;
    }

    // ==========================================================================
    // Queries — review queue
    // ==========================================================================

    public async Task<ReviewQueueData> GetReviewQueueAsync(CancellationToken ct = default)
    {
        var reviewableProfiles = (await _profileService.GetReviewableProfilesAsync(ct)).ToList();

        var allUserIds = reviewableProfiles.Select(p => p.UserId).ToList();
        var pendingAppUserIds = await _applicationDecisionService
            .GetUserIdsWithPendingApplicationAsync(allUserIds, ct);

        var consentProgress = new Dictionary<Guid, ConsentProgressInfo>();
        foreach (var userId in allUserIds)
        {
            var snapshot = await _membershipCalculator.GetMembershipSnapshotAsync(userId, ct);
            consentProgress[userId] = new ConsentProgressInfo(
                snapshot.RequiredConsentCount - snapshot.PendingConsentCount,
                snapshot.RequiredConsentCount);
        }

        var flagged = reviewableProfiles
            .Where(p => p.ConsentCheckStatus == ConsentCheckStatus.Flagged)
            .ToList();
        var pending = reviewableProfiles.Except(flagged).ToList();

        // ReviewQueueData currently types PendingAppUserIds as HashSet<Guid> —
        // materialize from the IReadOnlySet<Guid> returned by Governance.
        var pendingAppHashSet = pendingAppUserIds.ToHashSet();

        return new ReviewQueueData(pending, flagged, pendingAppHashSet, consentProgress);
    }

    public async Task<ReviewDetailData> GetReviewDetailAsync(Guid userId, CancellationToken ct = default)
    {
        var profile = await _profileService.GetProfileAsync(userId, ct);

        if (profile is null)
            return new ReviewDetailData(null, 0, 0, null);

        var snapshot = await _membershipCalculator.GetMembershipSnapshotAsync(userId, ct);

        var pendingApp = await _applicationDecisionService
            .GetSubmittedApplicationForUserAsync(userId, ct);

        return new ReviewDetailData(
            profile,
            snapshot.RequiredConsentCount - snapshot.PendingConsentCount,
            snapshot.RequiredConsentCount,
            pendingApp);
    }

    // ==========================================================================
    // Queries — Board voting
    // ==========================================================================

    public Task<BoardVotingDashboardData> GetBoardVotingDashboardAsync(
        CancellationToken ct = default) =>
        _applicationDecisionService.GetBoardVotingDashboardAsync(ct);

    public Task<BoardVotingDetailData?> GetBoardVotingDetailAsync(
        Guid applicationId, CancellationToken ct = default) =>
        _applicationDecisionService.GetBoardVotingDetailAsync(applicationId, ct);

    // ==========================================================================
    // Consent-check mutations
    // ==========================================================================

    public async Task<OnboardingResult> ClearConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default)
    {
        // Profile mutation + cache invalidation owned by ProfileService/decorator.
        var result = await _profileService.ClearConsentCheckAsync(userId, reviewerId, notes, ct);
        if (!result.Success)
            return result;

        // Sync Volunteers team membership (adds to team if consents are also complete)
        await _syncJob.SyncVolunteersMembershipForUserAsync(userId, CancellationToken.None);

        // If user already has approved tier applications, sync those teams too.
        var approvedTiers = await _applicationDecisionService.GetApprovedTiersForUserAsync(userId, ct);

        foreach (var tier in approvedTiers)
        {
            if (tier == MembershipTier.Colaborador)
                await _syncJob.SyncColaboradorsMembershipForUserAsync(userId, CancellationToken.None);
            else if (tier == MembershipTier.Asociado)
                await _syncJob.SyncAsociadosMembershipForUserAsync(userId, CancellationToken.None);
        }

        return result;
    }

    public async Task<OnboardingResult> FlagConsentCheckAsync(
        Guid userId, Guid reviewerId, string? notes, CancellationToken ct = default)
    {
        var result = await _profileService.FlagConsentCheckAsync(userId, reviewerId, notes, ct);
        if (!result.Success)
            return result;

        await DeprovisionApprovalGatedSystemTeamsAsync(userId);
        return result;
    }

    // ==========================================================================
    // Board vote
    // ==========================================================================

    public Task<bool> HasBoardVotesAsync(Guid applicationId, CancellationToken ct = default) =>
        _applicationDecisionService.HasBoardVotesAsync(applicationId, ct);

    public async Task<OnboardingResult> CastBoardVoteAsync(
        Guid applicationId, Guid boardMemberUserId, VoteChoice vote, string? note,
        CancellationToken ct = default)
    {
        var result = await _applicationDecisionService.CastBoardVoteAsync(
            applicationId, boardMemberUserId, vote, note, ct);
        // Map ApplicationDecisionResult → OnboardingResult so callers keep a
        // single error-key vocabulary during the migration.
        return new OnboardingResult(result.Success, result.ErrorKey);
    }

    // ==========================================================================
    // Signup reject / volunteer approve
    // ==========================================================================

    public async Task<OnboardingResult> RejectSignupAsync(
        Guid userId, Guid reviewerId, string? reason, CancellationToken ct = default)
    {
        var result = await _profileService.RejectSignupAsync(userId, reviewerId, reason, ct);
        if (!result.Success)
            return result;

        await DeprovisionApprovalGatedSystemTeamsAsync(userId);

        var rejectUser = await _userService.GetByIdAsync(userId, ct);

        try
        {
            await _emailService.SendSignupRejectedAsync(
                rejectUser?.Email ?? string.Empty,
                rejectUser?.DisplayName ?? string.Empty,
                reason,
                rejectUser?.PreferredLanguage ?? "en");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send signup rejection email to {UserId}", userId);
        }

        try
        {
            await _notificationService.SendAsync(
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
            _logger.LogError(ex, "Failed to dispatch ProfileRejected notification for user {UserId}", userId);
        }

        return result;
    }

    public async Task<OnboardingResult> ApproveVolunteerAsync(
        Guid userId, Guid adminId, CancellationToken ct = default)
    {
        // Pre-flight existence check so we keep the historical "NotFound" error
        // key contract — ApproveVolunteer used to gate on User + Profile both
        // existing; with the nav strip we gate on the profile via ProfileService.
        var result = await _profileService.ApproveVolunteerAsync(userId, adminId, ct);
        if (!result.Success)
            return result;

        // Sync Volunteers team membership (adds user if they also have all required consents)
        await _syncJob.SyncVolunteersMembershipForUserAsync(userId);

        _metrics.RecordVolunteerApproved();

        try
        {
            await _notificationService.SendAsync(
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
            _logger.LogError(ex, "Failed to dispatch VolunteerApproved notification for user {UserId}", userId);
        }

        return result;
    }

    // ==========================================================================
    // Suspend / unsuspend
    // ==========================================================================

    public async Task<OnboardingResult> SuspendAsync(
        Guid userId, Guid adminId, string? notes, CancellationToken ct = default)
    {
        var result = await _profileService.SuspendAsync(userId, adminId, notes, ct);
        if (!result.Success)
            return result;

        try
        {
            await _notificationService.SendAsync(
                NotificationSource.AccessSuspended,
                NotificationClass.Actionable,
                NotificationPriority.Critical,
                "Your access has been suspended",
                [userId],
                body: string.IsNullOrWhiteSpace(notes)
                    ? "Your access has been suspended by an administrator."
                    : $"Your access has been suspended: {notes}",
                actionUrl: "/Profile",
                actionLabel: "View profile",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch AccessSuspended notification for user {UserId}", userId);
        }

        _metrics.RecordMemberSuspended("admin");

        return result;
    }

    public async Task<OnboardingResult> UnsuspendAsync(
        Guid userId, Guid adminId, CancellationToken ct = default)
    {
        var result = await _profileService.UnsuspendAsync(userId, adminId, ct);
        if (!result.Success)
            return result;

        try
        {
            await _notificationInboxService.ResolveBySourceAsync(userId, NotificationSource.AccessSuspended, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve AccessSuspended notifications for user {UserId}", userId);
        }

        return result;
    }

    // ==========================================================================
    // Consent-check pending (shared: ConsentService + ProfileService call this)
    // ==========================================================================

    public async Task<bool> SetConsentCheckPendingIfEligibleAsync(
        Guid userId, CancellationToken ct = default)
    {
        var profile = await _profileService.GetProfileAsync(userId, ct);
        if (profile is null || profile.IsApproved || profile.ConsentCheckStatus is not null)
            return false;

        var hasAllConsents = await _membershipCalculator.HasAllRequiredConsentsForTeamAsync(
            userId, SystemTeamIds.Volunteers, ct);
        if (!hasAllConsents)
            return false;

        var set = await _profileService.SetConsentCheckPendingAsync(userId, ct);
        if (!set)
            return false;

        try
        {
            await _notificationService.SendToRoleAsync(
                NotificationSource.ConsentReviewNeeded,
                NotificationClass.Actionable,
                NotificationPriority.High,
                "New consent review needed",
                RoleNames.ConsentCoordinator,
                body: "A human has completed all required consents and needs review.",
                actionUrl: "/OnboardingReview",
                actionLabel: "Review →",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch ConsentReviewNeeded notification for user {UserId}", userId);
        }

        return true;
    }

    // ==========================================================================
    // Badge counts
    // ==========================================================================

    public Task<int> GetPendingReviewCountAsync(CancellationToken ct = default) =>
        _profileService.GetPendingReviewCountAsync(ct);

    public Task<int> GetUnvotedApplicationCountAsync(
        Guid boardMemberUserId, CancellationToken ct = default) =>
        _applicationDecisionService.GetUnvotedApplicationCountAsync(boardMemberUserId, ct);

    // ==========================================================================
    // Admin dashboard
    // ==========================================================================

    public async Task<AdminDashboardData> GetAdminDashboardAsync(CancellationToken ct = default)
    {
        var allUserIds = await _userService.GetAllUserIdsAsync(ct);
        var totalMembers = allUserIds.Count;
        var partition = await _membershipCalculator.PartitionUsersAsync(allUserIds, ct);

        var pendingApplications =
            await _applicationDecisionService.GetPendingApplicationCountAsync(ct);
        var appStats = await _applicationDecisionService.GetAdminStatsAsync(ct);

        // Language distribution for the admin dashboard chart — approved,
        // non-suspended humans, grouped by PreferredLanguage. Union
        // Active + MissingConsents; pending-deletion users are not counted
        // (bucket is split off earlier by PartitionUsersAsync). This is a
        // visualization, not an audit count, so sub-user-count drift from
        // the pre-partition predicate is acceptable. Pass to the User
        // section, which owns preferred language — no cross-domain join
        // (design-rules §6).
        var approvedNotSuspended = partition.Active
            .Concat(partition.MissingConsents)
            .ToList();
        var rawLanguageDistribution = await _userService.GetLanguageDistributionForUserIdsAsync(
            approvedNotSuspended, ct);
        var languageDistribution = rawLanguageDistribution
            .Select(x => new LanguageCount(x.Language, x.Count))
            .ToList();

        return new AdminDashboardData(
            totalMembers,
            partition.IncompleteSignup.Count,
            partition.PendingApproval.Count,
            partition.Active.Count,
            partition.MissingConsents.Count,
            partition.Suspended.Count,
            partition.PendingDeletion.Count,
            pendingApplications,
            appStats.Total,
            appStats.Approved,
            appStats.Rejected,
            appStats.ColaboradorApplied,
            appStats.AsociadoApplied,
            languageDistribution);
    }

    // ==========================================================================
    // Purge
    // ==========================================================================

    public async Task<OnboardingResult> PurgeHumanAsync(Guid userId, CancellationToken ct = default)
    {
        var (purged, _) = await _userService.PurgeAsync(userId, ct);
        if (!purged)
            return new OnboardingResult(false, "NotFound");

        return new OnboardingResult(true);
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private async Task DeprovisionApprovalGatedSystemTeamsAsync(Guid userId)
    {
        await _syncJob.SyncVolunteersMembershipForUserAsync(userId, CancellationToken.None);
        await _syncJob.SyncColaboradorsMembershipForUserAsync(userId, CancellationToken.None);
        await _syncJob.SyncAsociadosMembershipForUserAsync(userId, CancellationToken.None);
    }
}
