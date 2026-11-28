using Humans.Auth.Contracts;
using Humans.Base.Threading;
using Humans.Gdpr.Contracts;
using Humans.Governance.Domain;
using Humans.Governance.Data;
using NodaTime;
using Humans.Governance.Services.Dtos;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Base.Constants;
using MemberApplication = Humans.Governance.Domain.Application;
using Humans.Audit.Contracts;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;

using System.Globalization;
using System.Resources;

namespace Humans.Governance.Services;

internal sealed class ApplicationDecisionService(
    IApplicationRepository repository,
    IUserService userService,
    ISystemRoleService systemRoleService,
    IAuditLogService auditLogService,
    IEmailService emailService,
    GovernanceEmails emailMessages,
    INotificationEmitter notificationService,
    GovernanceBodySync bodySync,
    IHumansMetrics metrics,
    CachingGovernanceService tierCache,
    CachingGovernanceManagement badgeCache,
    IClock clock,
    ILogger<ApplicationDecisionService> logger) : IGovernanceManagement, IUserDataContributor, IUserMerge
{
    private static readonly TrackedLock SubmissionLock = new("Governance.SubmitApplication");

    internal const string Applications = "Applications";

    public async Task<ApplicationDecisionResult> ApproveAsync(
        Guid applicationId,
        Guid reviewerUserId,
        string? notes,
        LocalDate boardMeetingDate,
        CancellationToken cancellationToken = default)
    {
        var (candidate, error) = await LoadDecisionApplicationAsync(applicationId, cancellationToken);
        if (error is not null) return new ApplicationDecisionResult(false, error);
        var application = candidate!;

        if (notes?.Length > 4000)
        {
            logger.LogWarning("Rejected overlong decision note for application {ApplicationId}", applicationId);
            return new ApplicationDecisionResult(false, "DecisionNoteTooLong");
        }

        var today = clock.GetCurrentInstant().InUtc().Date;
        application.Approve(reviewerUserId, notes, clock);
        application.BoardMeetingDate = boardMeetingDate;

        application.TermExpiresAt = TermExpiryCalculator.ComputeTermExpiry(today);

        await FinalizeDecisionAsync(application, reviewerUserId, cancellationToken);

        if (application.MembershipTier == MembershipTier.Colaborador)
            await bodySync.RequestAsync(GovernanceBody.Colaboradores, cancellationToken);
        else if (application.MembershipTier == MembershipTier.Asociado)
            await bodySync.RequestAsync(GovernanceBody.Asociados, cancellationToken);

        await SendApprovalAsync(application, cancellationToken);

        return new ApplicationDecisionResult(true);
    }

    public async Task<ApplicationDecisionResult> RejectAsync(
        Guid applicationId,
        Guid reviewerUserId,
        string reason,
        LocalDate boardMeetingDate,
        CancellationToken cancellationToken = default)
    {
        var (candidate, error) = await LoadDecisionApplicationAsync(applicationId, cancellationToken);
        if (error is not null) return new ApplicationDecisionResult(false, error);
        var application = candidate!;

        if (string.IsNullOrWhiteSpace(reason))
            return new ApplicationDecisionResult(false, "DecisionNoteRequired");

        if (reason.Length > 4000)
        {
            logger.LogWarning("Rejected overlong decision note for application {ApplicationId}", applicationId);
            return new ApplicationDecisionResult(false, "DecisionNoteTooLong");
        }

        application.Reject(reviewerUserId, reason, clock);
        application.BoardMeetingDate = boardMeetingDate;

        await FinalizeDecisionAsync(application, reviewerUserId, cancellationToken);

        await SendRejectionAsync(application, reason, cancellationToken);

        return new ApplicationDecisionResult(true);
    }

    private async Task<(MemberApplication? Application, string? Error)> LoadDecisionApplicationAsync(
        Guid applicationId, CancellationToken cancellationToken)
    {
        var application = await repository.GetByIdAsync(applicationId, cancellationToken);
        if (application is null) return (null, "NotFound");
        if (application.Status != ApplicationStatus.Submitted) return (null, "NotSubmitted");

        // Check before either decision mutates the application: finalization deletes Board votes.
        var voterIds = await repository.GetVoterIdsForApplicationAsync(applicationId, cancellationToken);
        return voterIds.Count == 0 ? (null, "NoVotes") : (application, null);
    }

    private async Task FinalizeDecisionAsync(
        MemberApplication application, Guid reviewerUserId, CancellationToken cancellationToken)
    {
        var approved = application.Status == ApplicationStatus.Approved;
        // Downgrades happen only at term expiry: an in-term higher tier stays, and the expiry job
        // falls back to this approval when it ends.
        var keepsHigherTier = approved && application.MembershipTier == MembershipTier.Colaborador
            && await repository.HasActiveApprovedTierAsync(
                application.UserId, MembershipTier.Asociado, clock.GetCurrentInstant().InUtc().Date, cancellationToken);
        var tierGrant = approved && !keepsHigherTier ? new MembershipTierRecord
        {
            UserId = application.UserId,
            Tier = application.MembershipTier,
            GrantedAt = clock.GetCurrentInstant(),
            ExpiresAt = application.TermExpiresAt,
            GrantedByApplicationId = application.Id,
        } : null;
        try
        {
            await repository.FinalizeAsync(application, cancellationToken, tierGrant);
        }
        finally
        {
            badgeCache.Clear();
            if (tierGrant is not null) tierCache.Clear();
        }

        var decision = approved ? "approved" : "rejected";
        await auditLogService.LogAsync(
            approved ? GovernanceAuditActions.TierApplicationApproved : GovernanceAuditActions.TierApplicationRejected,
            GovernanceAuditSubjectTypes.Application,
            application.Id,
            keepsHigherTier
                ? $"{application.MembershipTier} application {decision}; Asociado tier kept until its term ends"
                : $"{application.MembershipTier} application {decision}",
            reviewerUserId);

        metrics.RecordApplicationProcessed(decision);
        logger.LogInformation(
            "Application {ApplicationId} {Decision} by {UserId}",
            application.Id, decision, reviewerUserId);
    }

    public ApplicationDecisionResult ValidateSubmission(
        MembershipTier tier, string? motivation,
        string? significantContribution, string? roleUnderstanding, string? additionalInfo = null)
    {
        if (tier is not MembershipTier.Colaborador and not MembershipTier.Asociado)
            return new ApplicationDecisionResult(false, "InvalidTier");

        if (string.IsNullOrWhiteSpace(motivation))
            return new ApplicationDecisionResult(false, "MotivationRequired");

        if (tier == MembershipTier.Asociado && string.IsNullOrWhiteSpace(significantContribution))
            return new ApplicationDecisionResult(false, "SignificantContributionRequired");

        if (tier == MembershipTier.Asociado && string.IsNullOrWhiteSpace(roleUnderstanding))
            return new ApplicationDecisionResult(false, "RoleUnderstandingRequired");

        if (motivation.Length is < 50 or > 2000)
            return new ApplicationDecisionResult(false, "MotivationLength");

        if (additionalInfo?.Length > 1000)
            return new ApplicationDecisionResult(false, "AdditionalInfoTooLong");

        if (significantContribution?.Length > 2000)
            return new ApplicationDecisionResult(false, "SignificantContributionTooLong");

        if (roleUnderstanding?.Length > 2000)
            return new ApplicationDecisionResult(false, "RoleUnderstandingTooLong");

        return new ApplicationDecisionResult(true);
    }

    public async Task<ApplicationDecisionResult> SubmitAsync(
        Guid userId, MembershipTier tier, string motivation,
        string? additionalInfo, string? significantContribution, string? roleUnderstanding,
        string language, CancellationToken ct = default)
    {
        var fieldRules = ValidateSubmission(tier, motivation, significantContribution, roleUnderstanding, additionalInfo);
        if (!fieldRules.Success)
        {
            logger.LogWarning("Tier application submission refused for user {UserId}: {ErrorKey}", userId, fieldRules.ErrorKey);
            return fieldRules;
        }

        using var submission = await SubmissionLock.AcquireAsync(logger, ct);
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

        try
        {
            await repository.AddAsync(application, ct);
        }
        finally
        {
            badgeCache.Clear();
        }

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
        try
        {
            await repository.FinalizeAsync(application, ct);
        }
        finally
        {
            badgeCache.Clear();
        }

        await auditLogService.LogAsync(
            GovernanceAuditActions.TierApplicationWithdrawn,
            GovernanceAuditSubjectTypes.Application,
            application.Id,
            $"{application.MembershipTier} application withdrawn",
            userId);

        metrics.RecordApplicationProcessed("withdrawn");
        logger.LogInformation(
            "User {UserId} withdrew application {ApplicationId}",
            userId, applicationId);

        return new ApplicationDecisionResult(true);
    }

    /// <summary>
    /// Approved applications whose stored expiry disagrees with the calculator for their
    /// approval date. Temporary: repairs rows approved under the pre-September-2026 rule.
    /// </summary>
    public async Task<IReadOnlyList<TermExpiryDriftRow>> GetTermExpiryDriftAsync(CancellationToken ct = default)
    {
        var (approved, _) = await repository.GetFilteredAsync(ApplicationStatus.Approved, null, null, 1, int.MaxValue, ct);
        return approved
            .Where(a => a.ResolvedAt is not null)
            .Select(a =>
            {
                var resolvedOn = a.ResolvedAt!.Value.InUtc().Date;
                return new TermExpiryDriftRow(
                    a.Id, a.UserId, a.MembershipTier, resolvedOn, a.TermExpiresAt,
                    TermExpiryCalculator.ComputeTermExpiry(resolvedOn));
            })
            .Where(r => r.StoredExpiry != r.ExpectedExpiry)
            .OrderBy(r => r.ResolvedOn)
            .ToList();
    }

    /// <summary>Rewrites every drifted expiry to the calculator's value; one audit entry per row. Returns the count.</summary>
    public async Task<int> FixTermExpiryDriftAsync(Guid adminUserId, CancellationToken ct = default)
    {
        var drift = await GetTermExpiryDriftAsync(ct);
        foreach (var row in drift)
        {
            var application = await repository.GetByIdAsync(row.ApplicationId, ct);
            if (application is null)
                continue;

            application.TermExpiresAt = row.ExpectedExpiry;
            await repository.UpdateAsync(application, ct, syncTierExpiry: true);

            await auditLogService.LogAsync(
                GovernanceAuditActions.TierTermExpiryCorrected,
                GovernanceAuditSubjectTypes.Application,
                application.Id,
                $"{application.MembershipTier} term expiry {row.StoredExpiry.ToInvariantDate() ?? "null"} -> {row.ExpectedExpiry.ToInvariantDate()}",
                adminUserId);
        }

        logger.LogInformation("Term expiry corrected on {Count} applications by {UserId}", drift.Count, adminUserId);
        return drift.Count;
    }

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

        if (note?.Length > 4000)
        {
            logger.LogWarning("Rejected overlong board vote note for application {ApplicationId}", applicationId);
            return new ApplicationDecisionResult(false, "NoteTooLong");
        }

        var now = clock.GetCurrentInstant();
        try
        {
            await repository.UpsertBoardVoteAsync(applicationId, boardMemberUserId, vote, note, now, ct);
        }
        finally
        {
            badgeCache.Invalidate(boardMemberUserId);
        }

        logger.LogInformation(
            "Board member {UserId} voted {Vote} on application {ApplicationId}",
            boardMemberUserId, vote, applicationId);

        return new ApplicationDecisionResult(true);
    }

    public Task<int> GetUnvotedApplicationCountAsync(
        Guid boardMemberUserId, CancellationToken ct = default) =>
        repository.GetUnvotedCountForBoardMemberAsync(boardMemberUserId, ct);

    public async Task<IReadOnlyList<ApplicationRenewalReminderCandidate>> GetExpiringApplicationsNeedingReminderAsync(
        LocalDate today, LocalDate reminderThreshold, CancellationToken ct = default)
    {
        var applications = await repository.GetExpiringApplicationsNeedingReminderAsync(
            today, reminderThreshold, ct);
        return applications
            .Select(application => new ApplicationRenewalReminderCandidate(
                application.Id,
                application.UserId,
                application.MembershipTier,
                application.TermExpiresAt))
            .ToList();
    }

    public Task<IReadOnlySet<(Guid UserId, MembershipTier Tier)>> GetPendingApplicationUserTiersAsync(
        CancellationToken ct = default) =>
        repository.GetPendingApplicationUserTiersAsync(ct);

    public Task MarkRenewalReminderSentAsync(
        Guid applicationId, Instant sentAt, CancellationToken ct = default) =>
        repository.MarkRenewalReminderSentAsync(applicationId, sentAt, ct);

    public async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct)
    {
        try
        {
            await repository.ReassignApplicationsToUserAsync(sourceUserId, targetUserId, updatedAt, ct);
        }
        finally
        {
            badgeCache.Clear();
        }
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
            SubmittedAt = a.SubmittedAt.ToIso8601(),
            UpdatedAt = a.UpdatedAt.ToIso8601(),
            RenewalReminderSentAt = a.RenewalReminderSentAt.ToIso8601(),
            ResolvedAt = a.ResolvedAt.ToIso8601(),
            TermExpiresAt = a.TermExpiresAt.ToInvariantDate(),
            BoardMeetingDate = a.BoardMeetingDate.ToInvariantDate(),
            a.DecisionNote,
            StateHistory = a.StateHistory.OrderBy(sh => sh.ChangedAt).Select(sh => new
            {
                sh.Status,
                ChangedAt = sh.ChangedAt.ToIso8601(),
                sh.Notes
            })
        }).ToList();

        return [new UserDataSlice(Applications, shaped)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Applications] =
                "Partially retained: the tier, status, term and Board-meeting dates of each " +
                "application stay as the association's record of its membership decisions " +
                "(Ley Orgánica 1/2002 Art. 14 — register of members and book of Board acts; " +
                "GDPR Art. 17(3)(b)). Every free-text field the human wrote or that was written " +
                "about them — motivation, additional info, contribution, role understanding, " +
                "review and decision notes, state-history notes, and their own Board-vote notes " +
                "— is cleared."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        repository.ScrubFreeTextForUserAsync(userId, clock.GetCurrentInstant(), ct);

    private static readonly ResourceManager NoticeResources = new(typeof(GovernanceResource));

    internal async Task SendApprovalAsync(MemberApplication application, CancellationToken cancellationToken)
    {
        var (recipientId, culture) = await SendDecisionEmailAsync(
            application,
            "approval",
            (recipientEmail, user, language) => emailMessages.ApplicationApproved(
                user.Id,
                recipientEmail,
                user.BurnerName,
                application.MembershipTier,
                language),
            cancellationToken);

        await SendDecisionNotificationAsync(
            application,
            recipientId,
            GovernanceNotificationSources.ApplicationApproved,
            "Governance_Notification_ApplicationApproved",
            "Governance_Notification_ApplicationApprovedBody",
            "ApplicationApproved",
            culture,
            cancellationToken);
    }

    internal async Task SendRejectionAsync(MemberApplication application, string reason, CancellationToken cancellationToken)
    {
        var (recipientId, culture) = await SendDecisionEmailAsync(
            application,
            "rejection",
            (recipientEmail, user, language) => emailMessages.ApplicationRejected(
                user.Id,
                recipientEmail,
                user.BurnerName,
                application.MembershipTier,
                reason,
                language),
            cancellationToken);

        await SendDecisionNotificationAsync(
            application,
            recipientId,
            GovernanceNotificationSources.ApplicationRejected,
            "Governance_Notification_ApplicationRejected",
            "Governance_Notification_ApplicationRejectedBody",
            "ApplicationRejected",
            culture,
            cancellationToken);
    }

    private async Task<(Guid UserId, CultureInfo Culture)> SendDecisionEmailAsync(
        MemberApplication application,
        string decisionName,
        Func<string, UserInfo, string, EmailMessage> buildMessage,
        CancellationToken cancellationToken)
    {
        var recipientId = application.UserId;
        var culture = CultureInfo.GetCultureInfo("en");
        try
        {
            var user = await userService.GetUserInfoAsync(application.UserId, cancellationToken);
            if (user is null || user.IsTombstone)
            {
                logger.LogWarning("Skipping {DecisionName} email for application {ApplicationId}: user {UserId} is unavailable",
                    decisionName, application.Id, application.UserId);
                return (recipientId, culture);
            }

            recipientId = user.Id;
            culture = CultureInfo.GetCultureInfo(user.PreferredLanguage.IsSupportedCultureCode() ? user.PreferredLanguage : "en");

            var recipientEmail = user.Email;
            if (!string.IsNullOrWhiteSpace(recipientEmail))
            {
                await emailService.SendAsync(buildMessage(recipientEmail, user, culture.Name));
            }
            else
            {
                logger.LogWarning(
                    "Skipping {DecisionName} email for application {ApplicationId} because user {UserId} has no notification-target email",
                    decisionName, application.Id, application.UserId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to send {DecisionName} email for {ApplicationId}",
                decisionName, application.Id);
        }
        return (recipientId, culture);
    }

    private async Task SendDecisionNotificationAsync(
        MemberApplication application,
        Guid recipientUserId,
        NotificationSourceDefinition source,
        string titleKey,
        string bodyKey,
        string logName,
        CultureInfo culture,
        CancellationToken cancellationToken)
    {
        try
        {
            await notificationService.SendAsync(
                source,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                string.Format(culture, NoticeResources.GetString(titleKey, culture)!, application.MembershipTier),
                [recipientUserId],
                body: string.Format(culture, NoticeResources.GetString(bodyKey, culture)!, application.MembershipTier),
                actionUrl: "/Governance/Applications",
                actionLabel: NoticeResources.GetString("Governance_Notification_ViewApplication", culture),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to dispatch {NotificationName} notification for {ApplicationId}",
                logName, application.Id);
        }
    }

    public async Task<IReadOnlyList<UserApplicationSnapshot>> GetUserApplicationsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var applications = await repository.GetByUserIdAsync(userId, ct);
        return applications
            .Select(application => new UserApplicationSnapshot(
                application.Id,
                application.UserId,
                application.Status,
                application.MembershipTier,
                application.SubmittedAt,
                application.ResolvedAt,
                application.TermExpiresAt))
            .ToList();
    }

    public async Task<ApplicationUserDetailDto?> GetUserApplicationDetailAsync(
        Guid applicationId, Guid userId, CancellationToken ct = default)
    {
        var application = await repository.GetByIdAsync(applicationId, ct);
        if (application is null || application.UserId != userId)
            return null;

        var historyDtos = ProjectHistory(application);

        return new ApplicationUserDetailDto(
            Id: application.Id,
            Status: application.Status,
            MembershipTier: application.MembershipTier,
            Motivation: application.Motivation,
            AdditionalInfo: application.AdditionalInfo,
            SignificantContribution: application.SignificantContribution,
            RoleUnderstanding: application.RoleUnderstanding,
            SubmittedAt: application.SubmittedAt,
            ResolvedAt: application.ResolvedAt,
            ReviewedByUserId: application.ReviewedByUserId,
            DecisionNote: application.DecisionNote,
            History: historyDtos);
    }

    public async Task<(IReadOnlyList<ApplicationAdminRowDto> Items, int TotalCount)> GetFilteredApplicationsAsync(
        string? statusFilter, string? tierFilter, Guid? userId, int page, int pageSize, CancellationToken ct = default)
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

        var (apps, totalCount) = await repository.GetFilteredAsync(status, tier, userId, page, pageSize, ct);
        if (apps.Count == 0)
        {
            return ([], totalCount);
        }

        var userIds = apps.Select(a => a.UserId);
        var users = await userService.GetUserInfosAsync(userIds, ct);

        var rows = apps.Select(a =>
        {
            var user = users.GetValueOrDefault(a.UserId);
            return new ApplicationAdminRowDto(
                Id: a.Id,
                UserId: a.UserId,
                UserEmail: user?.Email ?? string.Empty,
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

        var applicant = await userService.GetUserInfoAsync(application.UserId, ct);
        var history = ProjectHistory(application);

        return new ApplicationAdminDetailDto(
            Id: application.Id,
            UserId: application.UserId,
            UserEmail: applicant?.Email ?? string.Empty,
            Status: application.Status,
            MembershipTier: application.MembershipTier,
            Motivation: application.Motivation,
            AdditionalInfo: application.AdditionalInfo,
            SignificantContribution: application.SignificantContribution,
            RoleUnderstanding: application.RoleUnderstanding,
            Language: application.Language,
            SubmittedAt: application.SubmittedAt,
            ReviewedByUserId: application.ReviewedByUserId,
            DecisionNote: application.DecisionNote,
            History: history);
    }

    public async Task<BoardVotingDashboardData> GetBoardVotingDashboardAsync(
        CancellationToken ct = default)
    {
        var applications = await repository.GetAllSubmittedWithVotesAsync(ct);

        var rows = applications.Select(a => new BoardVotingDashboardRow(
            ApplicationId: a.Id,
            UserId: a.UserId,
            MembershipTier: a.MembershipTier,
            SubmittedAt: a.SubmittedAt,
            Votes: ProjectBoardVotes(a))).ToList();

        var boardMemberIds = await systemRoleService.GetActiveUserIdsInRoleAsync(
            RoleNames.Board, ct);
        var boardUsersById = await userService.GetUserInfosAsync(boardMemberIds, ct);
        var boardMembers = boardUsersById.Values
            .Select(u => new BoardMemberInfo(u.Id, u.BurnerName))
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

        return new BoardVotingDetailData(
            ApplicationId: application.Id,
            UserId: application.UserId,
            FirstName: applicantInfo?.FirstName ?? string.Empty,
            LastName: applicantInfo?.LastName ?? string.Empty,
            MembershipTier: application.MembershipTier,
            Motivation: application.Motivation,
            AdditionalInfo: application.AdditionalInfo,
            SignificantContribution: application.SignificantContribution,
            RoleUnderstanding: application.RoleUnderstanding,
            SubmittedAt: application.SubmittedAt,
            Votes: ProjectBoardVotes(application));
    }

    public async Task<ApplicationAdminStats> GetAdminStatsAsync(CancellationToken ct = default)
    {
        var applications = await repository.GetNonWithdrawnAsync(ct);
        return new ApplicationAdminStats(
            applications.Count,
            applications.Count(a => a.Status == ApplicationStatus.Approved),
            applications.Count(a => a.Status == ApplicationStatus.Rejected),
            applications.Count(a => a.MembershipTier == MembershipTier.Colaborador),
            applications.Count(a => a.MembershipTier == MembershipTier.Asociado));
    }

    public Task<int> GetPendingApplicationCountAsync(CancellationToken ct = default) =>
        repository.CountByStatusAsync(ApplicationStatus.Submitted, ct);

    private static IReadOnlyList<BoardVoteRow> ProjectBoardVotes(MemberApplication application) =>
        application.BoardVotes
            .Select(vote => new BoardVoteRow(
                BoardMemberUserId: vote.BoardMemberUserId,
                Vote: vote.Vote,
                Note: vote.Note,
                VotedAt: vote.VotedAt))
            .ToList();

    /// <summary>Submission, then the resolution once there is one: a withdrawal is the applicant's, a decision the
    /// reviewer's.</summary>
    private static IReadOnlyList<ApplicationStateHistoryDto> ProjectHistory(MemberApplication application)
    {
        var submitted = new ApplicationStateHistoryDto(
            ApplicationStatus.Submitted, application.SubmittedAt, application.UserId, Notes: null);
        if (application.ResolvedAt is not { } resolvedAt)
            return [submitted];

        var resolution = new ApplicationStateHistoryDto(
            application.Status, resolvedAt, application.ReviewedByUserId ?? application.UserId, application.DecisionNote);
        return [submitted, resolution];
    }
}
