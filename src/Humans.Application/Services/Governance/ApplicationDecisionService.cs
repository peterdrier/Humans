using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.DTOs.Governance;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using MemberApplication = Humans.Domain.Entities.Application;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Services.Governance;

public sealed class ApplicationDecisionService(
    IApplicationRepository repository,
    IUserService userService,
    IProfileService profileService,
    IRoleAssignmentService roleAssignmentService,
    IAuditLogService auditLogService,
    IEmailService emailService,
    IUserEmailService userEmailService,
    INotificationService notificationService,
    ISystemTeamSync syncJob,
    IHumansMetrics metrics,
    INavBadgeCacheInvalidator navBadge,
    INotificationMeterCacheInvalidator notificationMeter,
    IVotingBadgeCacheInvalidator votingBadge,
    IClock clock,
    ILogger<ApplicationDecisionService> logger) : IApplicationDecisionService, IUserDataContributor, IUserMerge
{
    public async Task<ApplicationDecisionResult> ApproveAsync(
        Guid applicationId,
        Guid reviewerUserId,
        string? notes,
        LocalDate? boardMeetingDate,
        CancellationToken cancellationToken = default)
    {
        var application = await repository.GetByIdAsync(applicationId, cancellationToken);
        if (application is null)
            return new ApplicationDecisionResult(false, "NotFound");

        if (application.Status != ApplicationStatus.Submitted)
            return new ApplicationDecisionResult(false, "NotSubmitted");

        // Ordering: capture voter ids → finalize (atomic governance commit) → audit → tier/sync → notifications.
        // Voter capture must precede FinalizeAsync (which deletes BoardVote rows).
        var voterIds = await repository.GetVoterIdsForApplicationAsync(applicationId, cancellationToken);

        application.Approve(reviewerUserId, notes, clock);
        application.BoardMeetingDate = boardMeetingDate;
        application.DecisionNote = notes;

        var today = clock.GetCurrentInstant().InUtc().Date;
        application.TermExpiresAt = TermExpiryCalculator.ComputeTermExpiry(today);

        await repository.FinalizeAsync(application, cancellationToken);

        navBadge.Invalidate();
        notificationMeter.Invalidate();
        foreach (var voterId in voterIds)
            votingBadge.Invalidate(voterId);

        await auditLogService.LogAsync(
            AuditAction.TierApplicationApproved,
            nameof(Domain.Entities.Application),
            application.Id,
            $"{application.MembershipTier} application approved",
            reviewerUserId);

        metrics.RecordApplicationProcessed("approved");
        logger.LogInformation(
            "Application {ApplicationId} approved by {UserId}",
            application.Id, reviewerUserId);

        await profileService.SetMembershipTierAsync(
            application.UserId, application.MembershipTier, cancellationToken);

        if (application.MembershipTier == MembershipTier.Colaborador)
            await syncJob.SyncMembershipForUserAsync(
                application.UserId, SystemTeamType.Colaboradors, cancellationToken);
        else if (application.MembershipTier == MembershipTier.Asociado)
            await syncJob.SyncMembershipForUserAsync(
                application.UserId, SystemTeamType.Asociados, cancellationToken);

        var user = await userService.GetUserInfoAsync(application.UserId, cancellationToken);
        if (user is not null)
        {
            var notificationEmails = await userEmailService.GetNotificationTargetEmailsAsync(
                [application.UserId], cancellationToken);
            if (notificationEmails.TryGetValue(application.UserId, out var recipientEmail)
                && !string.IsNullOrWhiteSpace(recipientEmail))
            {
                try
                {
                    await emailService.SendApplicationApprovedAsync(
                        recipientEmail,
                        user.BurnerName,
                        application.MembershipTier,
                        user.PreferredLanguage);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to send approval email for {ApplicationId}", application.Id);
                }
            }
            else
            {
                logger.LogWarning(
                    "Skipping approval email for application {ApplicationId} because user {UserId} has no notification-target email",
                    application.Id, application.UserId);
            }
        }

        try
        {
            await notificationService.SendAsync(
                NotificationSource.ApplicationApproved,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"Your {application.MembershipTier} application has been approved",
                [application.UserId],
                body: $"Congratulations! Your {application.MembershipTier} application has been approved.",
                actionUrl: "/Governance/MyApplications",
                actionLabel: "View application",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to dispatch ApplicationApproved notification for {ApplicationId}",
                application.Id);
        }

        return new ApplicationDecisionResult(true);
    }

    public async Task<ApplicationDecisionResult> RejectAsync(
        Guid applicationId,
        Guid reviewerUserId,
        string reason,
        LocalDate? boardMeetingDate,
        CancellationToken cancellationToken = default)
    {
        var application = await repository.GetByIdAsync(applicationId, cancellationToken);
        if (application is null)
            return new ApplicationDecisionResult(false, "NotFound");

        if (application.Status != ApplicationStatus.Submitted)
            return new ApplicationDecisionResult(false, "NotSubmitted");

        // Capture voter ids before FinalizeAsync deletes them.
        var voterIds = await repository.GetVoterIdsForApplicationAsync(applicationId, cancellationToken);

        application.Reject(reviewerUserId, reason, clock);
        application.BoardMeetingDate = boardMeetingDate;
        application.DecisionNote = reason;

        await repository.FinalizeAsync(application, cancellationToken);

        navBadge.Invalidate();
        notificationMeter.Invalidate();
        foreach (var voterId in voterIds)
            votingBadge.Invalidate(voterId);

        await auditLogService.LogAsync(
            AuditAction.TierApplicationRejected,
            nameof(Domain.Entities.Application),
            application.Id,
            $"{application.MembershipTier} application rejected",
            reviewerUserId);

        metrics.RecordApplicationProcessed("rejected");
        logger.LogInformation(
            "Application {ApplicationId} rejected by {UserId}",
            application.Id, reviewerUserId);

        var user = await userService.GetUserInfoAsync(application.UserId, cancellationToken);
        if (user is not null)
        {
            var notificationEmails = await userEmailService.GetNotificationTargetEmailsAsync(
                [application.UserId], cancellationToken);
            if (notificationEmails.TryGetValue(application.UserId, out var recipientEmail)
                && !string.IsNullOrWhiteSpace(recipientEmail))
            {
                try
                {
                    await emailService.SendApplicationRejectedAsync(
                        recipientEmail,
                        user.BurnerName,
                        application.MembershipTier,
                        reason,
                        user.PreferredLanguage);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to send rejection email for {ApplicationId}", application.Id);
                }
            }
            else
            {
                logger.LogWarning(
                    "Skipping rejection email for application {ApplicationId} because user {UserId} has no notification-target email",
                    application.Id, application.UserId);
            }
        }

        try
        {
            await notificationService.SendAsync(
                NotificationSource.ApplicationRejected,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"Your {application.MembershipTier} application was not approved",
                [application.UserId],
                body: $"Your {application.MembershipTier} application was not approved.",
                actionUrl: "/Governance/MyApplications",
                actionLabel: "View application",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to dispatch ApplicationRejected notification for {ApplicationId}",
                application.Id);
        }

        return new ApplicationDecisionResult(true);
    }

    public async Task<IReadOnlyList<UserApplicationSnapshot>> GetUserApplicationsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var applications = await repository.GetByUserIdAsync(userId, ct);
        return applications.Select(ToUserApplicationSnapshot).ToList();
    }

    private static UserApplicationSnapshot ToUserApplicationSnapshot(MemberApplication application) =>
        new(
            application.Id,
            application.UserId,
            application.Status,
            application.MembershipTier,
            application.SubmittedAt,
            application.ResolvedAt,
            application.TermExpiresAt,
            application.Motivation,
            application.AdditionalInfo,
            application.SignificantContribution,
            application.RoleUnderstanding);

    public async Task<ApplicationUserDetailDto?> GetUserApplicationDetailAsync(
        Guid applicationId, Guid userId, CancellationToken ct = default)
    {
        var application = await repository.GetByIdAsync(applicationId, ct);
        if (application is null || application.UserId != userId)
            return null;

        var (reviewerName, historyDtos) = await StitchHistoryAsync(application, ct);

        return new ApplicationUserDetailDto(
            Id: application.Id,
            UserId: application.UserId,
            Status: application.Status,
            MembershipTier: application.MembershipTier,
            Motivation: application.Motivation,
            AdditionalInfo: application.AdditionalInfo,
            SignificantContribution: application.SignificantContribution,
            RoleUnderstanding: application.RoleUnderstanding,
            SubmittedAt: application.SubmittedAt,
            ReviewStartedAt: application.ReviewStartedAt,
            ResolvedAt: application.ResolvedAt,
            ReviewerName: reviewerName,
            ReviewNotes: application.ReviewNotes,
            History: historyDtos);
    }

    public async Task<ApplicationDecisionResult> SubmitAsync(
        Guid userId, MembershipTier tier, string motivation,
        string? additionalInfo, string? significantContribution, string? roleUnderstanding,
        string language, CancellationToken ct = default)
    {
        if (tier == MembershipTier.Volunteer)
            return new ApplicationDecisionResult(false, "InvalidTier");

        if (tier == MembershipTier.Asociado && string.IsNullOrWhiteSpace(significantContribution))
            return new ApplicationDecisionResult(false, "SignificantContributionRequired");

        if (tier == MembershipTier.Asociado && string.IsNullOrWhiteSpace(roleUnderstanding))
            return new ApplicationDecisionResult(false, "RoleUnderstandingRequired");

        var hasPending = await repository.AnySubmittedForUserAsync(userId, ct);
        if (hasPending)
            return new ApplicationDecisionResult(false, "AlreadyPending");

        var now = clock.GetCurrentInstant();
        var application = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = tier,
            Motivation = motivation,
            AdditionalInfo = additionalInfo,
            SignificantContribution = tier == MembershipTier.Asociado ? significantContribution : null,
            RoleUnderstanding = tier == MembershipTier.Asociado ? roleUnderstanding : null,
            Language = language,
            SubmittedAt = now,
            UpdatedAt = now
        };
        application.ValidateTier();

        await repository.AddAsync(application, ct);

        navBadge.Invalidate();
        notificationMeter.Invalidate();

        logger.LogInformation(
            "User {UserId} submitted application {ApplicationId}",
            userId, application.Id);

        return new ApplicationDecisionResult(true, ApplicationId: application.Id);
    }

    public async Task<ApplicationDecisionResult> WithdrawAsync(
        Guid applicationId, Guid userId, CancellationToken ct = default)
    {
        var application = await repository.GetByIdAsync(applicationId, ct);
        if (application is null || application.UserId != userId)
            return new ApplicationDecisionResult(false, "NotFound");

        if (application.Status != ApplicationStatus.Submitted)
            return new ApplicationDecisionResult(false, "CannotWithdraw");

        application.Withdraw(clock);
        await repository.UpdateAsync(application, ct);

        navBadge.Invalidate();
        notificationMeter.Invalidate();

        metrics.RecordApplicationProcessed("withdrawn");
        logger.LogInformation(
            "User {UserId} withdrew application {ApplicationId}",
            userId, applicationId);

        return new ApplicationDecisionResult(true);
    }

    public async Task<(IReadOnlyList<ApplicationAdminRowDto> Items, int TotalCount)> GetFilteredApplicationsAsync(
        string? statusFilter, string? tierFilter, int page, int pageSize, CancellationToken ct = default)
    {
        ApplicationStatus? status = null;
        if (!string.IsNullOrWhiteSpace(statusFilter)
            && Enum.TryParse<ApplicationStatus>(statusFilter, ignoreCase: true, out var parsedStatus))
        {
            status = parsedStatus;
        }

        MembershipTier? tier = null;
        if (!string.IsNullOrWhiteSpace(tierFilter)
            && Enum.TryParse<MembershipTier>(tierFilter, ignoreCase: true, out var parsedTier))
        {
            tier = parsedTier;
        }

        var (apps, totalCount) = await repository.GetFilteredAsync(status, tier, page, pageSize, ct);
        if (apps.Count == 0)
        {
            return ([], totalCount);
        }

        var userIds = apps.Select(a => a.UserId).Distinct().ToList();
        var users = await userService.GetUserInfosAsync(userIds, ct);

        var rows = apps.Select(a =>
        {
            var user = users.GetValueOrDefault(a.UserId);
            return new ApplicationAdminRowDto(
                Id: a.Id,
                UserId: a.UserId,
                UserEmail: user?.Email ?? string.Empty,
                UserDisplayName: user?.BurnerName ?? string.Empty,
                Status: a.Status,
                MembershipTier: a.MembershipTier,
                SubmittedAt: a.SubmittedAt,
                Motivation: a.Motivation);
        }).ToList();

        return (rows, totalCount);
    }

    public async Task<ApplicationAdminDetailDto?> GetApplicationDetailAsync(
        Guid applicationId, CancellationToken ct = default)
    {
        var application = await repository.GetByIdAsync(applicationId, ct);
        if (application is null)
            return null;

        var userIds = new HashSet<Guid> { application.UserId };
        if (application.ReviewedByUserId is { } reviewerId)
            userIds.Add(reviewerId);
        foreach (var row in application.StateHistory)
            userIds.Add(row.ChangedByUserId);

        var users = await userService.GetUserInfosAsync(userIds, ct);

        var applicant = users.GetValueOrDefault(application.UserId);
        var reviewer = application.ReviewedByUserId is { } rid
            ? users.GetValueOrDefault(rid)
            : null;

        var history = application.StateHistory
            .OrderByDescending(h => h.ChangedAt)
            .Select(h => new ApplicationStateHistoryDto(
                Status: h.Status,
                ChangedAt: h.ChangedAt,
                ChangedByUserId: h.ChangedByUserId,
                ChangedByDisplayName: users.GetValueOrDefault(h.ChangedByUserId)?.BurnerName,
                Notes: h.Notes))
            .ToList();

        return new ApplicationAdminDetailDto(
            Id: application.Id,
            UserId: application.UserId,
            UserEmail: applicant?.Email ?? string.Empty,
            UserDisplayName: applicant?.BurnerName ?? string.Empty,
            UserProfilePictureUrl: applicant?.ProfilePictureUrl,
            Status: application.Status,
            MembershipTier: application.MembershipTier,
            Motivation: application.Motivation,
            AdditionalInfo: application.AdditionalInfo,
            SignificantContribution: application.SignificantContribution,
            RoleUnderstanding: application.RoleUnderstanding,
            Language: application.Language,
            SubmittedAt: application.SubmittedAt,
            ReviewStartedAt: application.ReviewStartedAt,
            ResolvedAt: application.ResolvedAt,
            ReviewerName: reviewer?.BurnerName,
            ReviewNotes: application.ReviewNotes,
            History: history);
    }

    // Onboarding-section support methods — Governance owns application/board-vote tables (design-rules §2c).
    public Task<IReadOnlySet<Guid>> GetUserIdsWithPendingApplicationAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        repository.GetUserIdsWithSubmittedAsync(userIds, ct);

    public async Task<SubmittedApplicationSnapshot?> GetSubmittedApplicationForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        var application = await repository.GetSubmittedForUserAsync(userId, ct);
        return application is null
            ? null
            : new SubmittedApplicationSnapshot(
                application.Id,
                application.MembershipTier,
                application.Motivation);
    }

    public Task<IReadOnlyList<MembershipTier>> GetApprovedTiersForUserAsync(
        Guid userId, CancellationToken ct = default) =>
        repository.GetApprovedTiersForUserAsync(userId, ct);

    public async Task<BoardVotingDashboardData> GetBoardVotingDashboardAsync(
        CancellationToken ct = default)
    {
        var applications = await repository.GetAllSubmittedWithVotesAsync(ct);

        var applicantIds = applications.Select(a => a.UserId).Distinct().ToList();
        var applicantsById = await userService.GetUserInfosAsync(applicantIds, ct);

        var rows = applications.Select(a =>
        {
            var applicant = applicantsById.GetValueOrDefault(a.UserId);
            return new BoardVotingDashboardRow(
                ApplicationId: a.Id,
                UserId: a.UserId,
                UserDisplayName: applicant?.BurnerName ?? string.Empty,
                UserProfilePictureUrl: applicant?.ProfilePictureUrl,
                MembershipTier: a.MembershipTier,
                ApplicationMotivation: a.Motivation,
                SubmittedAt: a.SubmittedAt,
                Status: a.Status,
                Votes: a.BoardVotes
                    .Select(v => new BoardVoteRow(
                        BoardMemberUserId: v.BoardMemberUserId,
                        BoardMemberDisplayName: null,
                        Vote: v.Vote,
                        Note: v.Note,
                        VotedAt: v.VotedAt))
                    .ToList());
        }).ToList();

        var boardMemberIds = await roleAssignmentService.GetActiveUserIdsInRoleAsync(
            RoleNames.Board, ct);
        var boardUsersById = await userService.GetUserInfosAsync(boardMemberIds, ct);
        var boardMembers = boardUsersById.Values
            .Select(u => new BoardMemberInfo(u.Id, u.BurnerName))
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BoardVotingDashboardData(rows, boardMembers);
    }

    public async Task<BoardVotingDetailData?> GetBoardVotingDetailAsync(
        Guid applicationId, CancellationToken ct = default)
    {
        var application = await repository.GetByIdAsync(applicationId, ct);
        if (application is null)
            return null;

        var applicantInfo = await userService.GetUserInfoAsync(application.UserId, ct);
        var profile = applicantInfo?.Profile;

        var voterIds = application.BoardVotes
            .Select(v => v.BoardMemberUserId)
            .Distinct()
            .ToList();
        var votersById = await userService.GetUserInfosAsync(voterIds, ct);

        var voteRows = application.BoardVotes
            .Select(v => new BoardVoteRow(
                BoardMemberUserId: v.BoardMemberUserId,
                BoardMemberDisplayName: votersById.GetValueOrDefault(v.BoardMemberUserId)?.BurnerName,
                Vote: v.Vote,
                Note: v.Note,
                VotedAt: v.VotedAt))
            .ToList();

        return new BoardVotingDetailData(
            ApplicationId: application.Id,
            UserId: application.UserId,
            DisplayName: applicantInfo?.BurnerName ?? string.Empty,
            ProfilePictureUrl: applicantInfo?.ProfilePictureUrl,
            Email: applicantInfo?.Email ?? string.Empty,
            FirstName: profile?.FirstName ?? string.Empty,
            LastName: profile?.LastName ?? string.Empty,
            City: profile?.City,
            CountryCode: profile?.CountryCode,
            MembershipTier: application.MembershipTier,
            Status: application.Status,
            Motivation: application.Motivation,
            AdditionalInfo: application.AdditionalInfo,
            SignificantContribution: application.SignificantContribution,
            RoleUnderstanding: application.RoleUnderstanding,
            SubmittedAt: application.SubmittedAt,
            Votes: voteRows);
    }

    public Task<bool> HasBoardVotesAsync(Guid applicationId, CancellationToken ct = default) =>
        repository.HasBoardVotesAsync(applicationId, ct);

    public async Task<ApplicationDecisionResult> CastBoardVoteAsync(
        Guid applicationId,
        Guid boardMemberUserId,
        VoteChoice vote,
        string? note,
        CancellationToken ct = default)
    {
        var application = await repository.GetByIdAsync(applicationId, ct);
        if (application is null)
            return new ApplicationDecisionResult(false, "NotFound");

        if (application.Status != ApplicationStatus.Submitted)
            return new ApplicationDecisionResult(false, "NotSubmitted");

        var now = clock.GetCurrentInstant();
        await repository.UpsertBoardVoteAsync(applicationId, boardMemberUserId, vote, note, now, ct);

        votingBadge.Invalidate(boardMemberUserId);

        logger.LogInformation(
            "Board member {UserId} voted {Vote} on application {ApplicationId}",
            boardMemberUserId, vote, applicationId);

        return new ApplicationDecisionResult(true);
    }

    public Task<int> GetUnvotedApplicationCountAsync(
        Guid boardMemberUserId, CancellationToken ct = default) =>
        repository.GetUnvotedCountForBoardMemberAsync(boardMemberUserId, ct);

    public Task<ApplicationAdminStats> GetAdminStatsAsync(CancellationToken ct = default) =>
        repository.GetAdminStatsAsync(ct);

    public Task<int> GetPendingApplicationCountAsync(CancellationToken ct = default) =>
        repository.CountByStatusAsync(ApplicationStatus.Submitted, ct);

    public async Task<IReadOnlyList<ApplicationRenewalReminderCandidate>> GetExpiringApplicationsNeedingReminderAsync(
        LocalDate today, LocalDate reminderThreshold, CancellationToken ct = default)
    {
        var applications = await repository.GetExpiringApplicationsNeedingReminderAsync(
            today, reminderThreshold, ct);
        return applications.Select(ToRenewalReminderCandidate).ToList();
    }

    private static ApplicationRenewalReminderCandidate ToRenewalReminderCandidate(MemberApplication application) =>
        new(
            application.Id,
            application.UserId,
            application.MembershipTier,
            application.SubmittedAt,
            application.TermExpiresAt);

    public Task<IReadOnlySet<(Guid UserId, MembershipTier Tier)>> GetPendingApplicationUserTiersAsync(
        CancellationToken ct = default) =>
        repository.GetPendingApplicationUserTiersAsync(ct);

    public Task MarkRenewalReminderSentAsync(
        Guid applicationId, Instant sentAt, CancellationToken ct = default) =>
        repository.MarkRenewalReminderSentAsync(applicationId, sentAt, ct);

    public async Task<IReadOnlyList<ApprovedApplicationDigestEntry>> GetApprovedInWindowAsync(
        Instant windowStart, Instant windowEnd, CancellationToken ct = default)
    {
        var applications = await repository.GetApprovedInWindowAsync(windowStart, windowEnd, ct);
        return applications
            .Select(a => new ApprovedApplicationDigestEntry(a.UserId, a.MembershipTier))
            .ToList();
    }

    public Task<IReadOnlyList<Guid>> GetSubmittedApplicationIdsAsync(
        CancellationToken ct = default) =>
        repository.GetSubmittedApplicationIdsAsync(ct);

    public Task<int> GetUnvotedCountForBoardMemberAmongApplicationsAsync(
        Guid boardMemberUserId,
        IReadOnlyCollection<Guid> applicationIds,
        CancellationToken ct = default) =>
        repository.GetUnvotedCountForBoardMemberAmongApplicationsAsync(
            boardMemberUserId, applicationIds, ct);

    public Task<IReadOnlyList<Guid>> GetActiveApprovedTierUserIdsAsync(
        MembershipTier tier, LocalDate today, CancellationToken ct = default) =>
        repository.GetActiveApprovedTierUserIdsAsync(tier, today, ct);

    public Task<bool> HasActiveApprovedTierAsync(
        Guid userId, MembershipTier tier, LocalDate today, CancellationToken ct = default) =>
        repository.HasActiveApprovedTierAsync(userId, tier, today, ct);

    public Task<IReadOnlyDictionary<Guid, MembershipTier>> GetOtherActiveTierAssignmentsAsync(
        MembershipTier excludeTier, LocalDate today, CancellationToken ct = default) =>
        repository.GetOtherActiveTierAssignmentsAsync(excludeTier, today, ct);

    public Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct) =>
        repository.ReassignApplicationsToUserAsync(sourceUserId, targetUserId, updatedAt, ct);

    public async Task UpdateDraftApplicationAsync(
        Guid applicationId, MembershipTier tier, string motivation,
        string? additionalInfo, string? significantContribution, string? roleUnderstanding,
        CancellationToken ct = default)
    {
        var application = await repository.GetByIdAsync(applicationId, ct);
        if (application is null)
        {
            logger.LogWarning("UpdateDraftApplicationAsync: application {Id} not found", applicationId);
            return;
        }

        if (application.Status != ApplicationStatus.Submitted)
        {
            logger.LogWarning(
                "UpdateDraftApplicationAsync: application {Id} is {Status}, not Submitted",
                applicationId, application.Status);
            return;
        }

        application.MembershipTier = tier;
        application.Motivation = motivation;
        application.AdditionalInfo = additionalInfo;
        application.SignificantContribution = significantContribution;
        application.RoleUnderstanding = roleUnderstanding;
        application.UpdatedAt = clock.GetCurrentInstant();

        await repository.UpdateAsync(application, ct);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var applications = await repository.GetByUserIdAsync(userId, ct);

        var shaped = applications.Select(a => new
        {
            a.Status,
            a.MembershipTier,
            a.Motivation,
            a.AdditionalInfo,
            a.SignificantContribution,
            a.RoleUnderstanding,
            a.Language,
            SubmittedAt = a.SubmittedAt.ToInvariantInstantString(),
            ResolvedAt = a.ResolvedAt.ToInvariantInstantString(),
            TermExpiresAt = a.TermExpiresAt.ToIsoDateString(),
            BoardMeetingDate = a.BoardMeetingDate.ToIsoDateString(),
            StateHistory = a.StateHistory.OrderBy(sh => sh.ChangedAt).Select(sh => new
            {
                sh.Status,
                ChangedAt = sh.ChangedAt.ToInvariantInstantString(),
                sh.Notes
            })
        }).ToList();

        return [new UserDataSlice(GdprExportSections.Applications, shaped)];
    }

    private async Task<(string? ReviewerName, IReadOnlyList<ApplicationStateHistoryDto> History)> StitchHistoryAsync(
        MemberApplication application, CancellationToken ct)
    {
        var userIds = new HashSet<Guid>();
        if (application.ReviewedByUserId is { } reviewerId)
            userIds.Add(reviewerId);
        foreach (var row in application.StateHistory)
            userIds.Add(row.ChangedByUserId);

        IReadOnlyDictionary<Guid, UserInfo> users = userIds.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await userService.GetUserInfosAsync(userIds, ct);

        var reviewerName = application.ReviewedByUserId is { } rid
            ? users.GetValueOrDefault(rid)?.BurnerName
            : null;

        var history = application.StateHistory
            .OrderByDescending(h => h.ChangedAt)
            .Select(h => new ApplicationStateHistoryDto(
                Status: h.Status,
                ChangedAt: h.ChangedAt,
                ChangedByUserId: h.ChangedByUserId,
                ChangedByDisplayName: users.GetValueOrDefault(h.ChangedByUserId)?.BurnerName,
                Notes: h.Notes))
            .ToList();

        return (reviewerName, history);
    }
}
