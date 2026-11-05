using Humans.Auth.Contracts;
using Humans.Base.Threading;
using Humans.Gdpr.Contracts;
using Humans.Governance.Domain;
using Humans.Governance.Data;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using Humans.Governance.Services.Dtos;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Governance.Contracts;
using Humans.Base.Constants;
using Humans.Base.Enums;
using MemberApplication = Humans.Governance.Domain.Application;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;

using Humans.Teams.Contracts;

namespace Humans.Governance.Services;

internal sealed class ApplicationDecisionService(
    IApplicationRepository repository,
    IUserService userService,
    MembershipTierService membershipTiers,
    IRoleAssignmentService roleAssignmentService,
    IAuditLogService auditLogService,
    IEmailService emailService,
    GovernanceEmails emailMessages,
    INotificationEmitter notificationService,
    ISystemTeamSync syncJob,
    IHumansMetrics metrics,
    IMemoryCache cache,
    IClock clock,
    ILogger<ApplicationDecisionService> logger) : IGovernanceManagement, IUserDataContributor, IUserMerge
{
    private readonly ApplicationReader reader = new(repository, userService, roleAssignmentService);
    private readonly ApplicationDecisionNotices notices = new(userService, emailService, emailMessages, notificationService, logger);
    private static readonly TrackedLock SubmissionLock = new("Governance.SubmitApplication");

    internal const string Applications = "Applications";

    private static readonly TimeSpan BadgeCacheDuration = TimeSpan.FromMinutes(2);

    public async Task<ApplicationDecisionResult> ApproveAsync(
        Guid applicationId,
        Guid reviewerUserId,
        string? notes,
        LocalDate? boardMeetingDate,
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
        // A finalized decision always carries its Board meeting date; a missing one means today.
        application.BoardMeetingDate = boardMeetingDate ?? today;
        application.DecisionNote = notes;

        application.TermExpiresAt = TermExpiryCalculator.ComputeTermExpiry(today);

        await FinalizeDecisionAsync(application, reviewerUserId, cancellationToken);

        if (application.MembershipTier == MembershipTier.Colaborador)
            await syncJob.SyncMembershipForUserAsync(
                application.UserId, SystemTeamType.Colaboradors, cancellationToken);
        else if (application.MembershipTier == MembershipTier.Asociado)
            await syncJob.SyncMembershipForUserAsync(
                application.UserId, SystemTeamType.Asociados, cancellationToken);

        await notices.SendApprovalAsync(application, cancellationToken);

        return new ApplicationDecisionResult(true);
    }

    public async Task<ApplicationDecisionResult> RejectAsync(
        Guid applicationId,
        Guid reviewerUserId,
        string reason,
        LocalDate? boardMeetingDate,
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
        application.BoardMeetingDate = boardMeetingDate ?? clock.GetCurrentInstant().InUtc().Date;
        application.DecisionNote = reason;

        await FinalizeDecisionAsync(application, reviewerUserId, cancellationToken);

        await notices.SendRejectionAsync(application, reason, cancellationToken);

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
        // Resolve the Board before the write so nothing awaitable sits between commit and audit.
        var boardMemberIds = await GetBoardMemberIdsAsync(cancellationToken);
        var approved = application.Status == ApplicationStatus.Approved;
        var tierGrant = approved ? new MembershipTierRecord
        {
            UserId = application.UserId,
            Tier = application.MembershipTier,
            GrantedAt = clock.GetCurrentInstant(),
            ExpiresAt = application.TermExpiresAt,
            GrantedByApplicationId = application.Id,
        } : null;
        await repository.FinalizeAsync(application, cancellationToken, tierGrant);

        InvalidateVotingBadges(boardMemberIds);

        var decision = approved ? "approved" : "rejected";
        await auditLogService.LogAsync(
            approved ? GovernanceAuditActions.TierApplicationApproved : GovernanceAuditActions.TierApplicationRejected,
            AuditEntityTypes.Application,
            application.Id,
            $"{application.MembershipTier} application {decision}",
            reviewerUserId);

        metrics.RecordApplicationProcessed(decision);
        logger.LogInformation(
            "Application {ApplicationId} {Decision} by {UserId}",
            application.Id, decision, reviewerUserId);
    }

    public Task<IReadOnlyList<UserApplicationSnapshot>> GetUserApplicationsAsync(
        Guid userId, CancellationToken ct = default) => reader.GetUserApplicationsAsync(userId, ct);

    public Task<ApplicationUserDetailDto?> GetUserApplicationDetailAsync(
        Guid applicationId, Guid userId, CancellationToken ct = default) =>
        reader.GetUserApplicationDetailAsync(applicationId, userId, ct);

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

        var boardMemberIds = await GetBoardMemberIdsAsync(ct);
        await repository.AddAsync(application, ct);

        InvalidateVotingBadges(boardMemberIds);

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
        var boardMemberIds = await GetBoardMemberIdsAsync(ct);
        await repository.FinalizeAsync(application, ct);

        InvalidateVotingBadges(boardMemberIds);

        metrics.RecordApplicationProcessed("withdrawn");
        logger.LogInformation(
            "User {UserId} withdrew application {ApplicationId}",
            userId, applicationId);

        return new ApplicationDecisionResult(true);
    }

    public Task<(IReadOnlyList<ApplicationAdminRowDto> Items, int TotalCount)> GetFilteredApplicationsAsync(
        string? statusFilter, string? tierFilter, int page, int pageSize, CancellationToken ct = default) =>
        reader.GetFilteredApplicationsAsync(statusFilter, tierFilter, page, pageSize, ct);

    /// <summary>
    /// Approved applications whose stored expiry disagrees with the calculator for their
    /// approval date. Temporary: repairs rows approved under the pre-September-2026 rule.
    /// </summary>
    public async Task<IReadOnlyList<TermExpiryDriftRow>> GetTermExpiryDriftAsync(CancellationToken ct = default)
    {
        var (approved, _) = await repository.GetFilteredAsync(ApplicationStatus.Approved, null, 1, int.MaxValue, ct);
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
                AuditEntityTypes.Application,
                application.Id,
                $"{application.MembershipTier} term expiry {row.StoredExpiry.ToInvariantDate() ?? "null"} -> {row.ExpectedExpiry.ToInvariantDate()}",
                adminUserId);
        }

        logger.LogInformation("Term expiry corrected on {Count} applications by {UserId}", drift.Count, adminUserId);
        return drift.Count;
    }

    public Task<ApplicationAdminDetailDto?> GetApplicationDetailAsync(
        Guid applicationId, CancellationToken ct = default) => reader.GetApplicationDetailAsync(applicationId, ct);

    public Task<IReadOnlySet<Guid>> GetUserIdsWithPendingApplicationAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        reader.GetUserIdsWithPendingApplicationAsync(userIds, ct);

    public Task<SubmittedApplicationSnapshot?> GetSubmittedApplicationForUserAsync(
        Guid userId, CancellationToken ct = default) => reader.GetSubmittedApplicationForUserAsync(userId, ct);

    public Task<BoardVotingDashboardData> GetBoardVotingDashboardAsync(CancellationToken ct = default) =>
        reader.GetBoardVotingDashboardAsync(ct);

    public Task<BoardVotingDetailData?> GetBoardVotingDetailAsync(Guid applicationId, CancellationToken ct = default) =>
        reader.GetBoardVotingDetailAsync(applicationId, ct);

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
        await repository.UpsertBoardVoteAsync(applicationId, boardMemberUserId, vote, note, now, ct);

        cache.Remove(VotingBadgeCacheKey(boardMemberUserId));

        logger.LogInformation(
            "Board member {UserId} voted {Vote} on application {ApplicationId}",
            boardMemberUserId, vote, applicationId);

        return new ApplicationDecisionResult(true);
    }

    public async Task<int> GetUnvotedApplicationCountAsync(
        Guid boardMemberUserId, CancellationToken ct = default) =>
        await cache.GetOrCreateAsync(VotingBadgeCacheKey(boardMemberUserId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = BadgeCacheDuration;
            return await repository.GetUnvotedCountForBoardMemberAsync(boardMemberUserId, ct);
        });

    private Task<IReadOnlyList<Guid>> GetBoardMemberIdsAsync(CancellationToken ct) =>
        roleAssignmentService.GetActiveUserIdsInRoleAsync(RoleNames.Board, ct);

    private void InvalidateVotingBadges(IReadOnlyList<Guid> boardMemberIds)
    {
        foreach (var boardMemberId in boardMemberIds)
            cache.Remove(VotingBadgeCacheKey(boardMemberId));
    }

    /// <summary>One Board member's unvoted-application count; this service is its only reader and evicter.</summary>
    internal static string VotingBadgeCacheKey(Guid boardMemberUserId) => $"NavBadge:Voting:{boardMemberUserId:N}";

    public Task<ApplicationAdminStats> GetAdminStatsAsync(CancellationToken ct = default) => reader.GetAdminStatsAsync(ct);

    public Task<int> GetPendingApplicationCountAsync(CancellationToken ct = default) => reader.GetPendingApplicationCountAsync(ct);

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
                application.SubmittedAt,
                application.TermExpiresAt))
            .ToList();
    }

    public Task<IReadOnlySet<(Guid UserId, MembershipTier Tier)>> GetPendingApplicationUserTiersAsync(
        CancellationToken ct = default) =>
        repository.GetPendingApplicationUserTiersAsync(ct);

    public Task MarkRenewalReminderSentAsync(
        Guid applicationId, Instant sentAt, CancellationToken ct = default) =>
        repository.MarkRenewalReminderSentAsync(applicationId, sentAt, ct);

    public Task<IReadOnlyList<Guid>> GetActiveApprovedTierUserIdsAsync(
        MembershipTier tier, LocalDate today, CancellationToken ct = default) =>
        repository.GetActiveApprovedTierUserIdsAsync(tier, today, ct);

    public Task<bool> HasActiveApprovedTierAsync(
        Guid userId, MembershipTier tier, LocalDate today, CancellationToken ct = default) =>
        repository.HasActiveApprovedTierAsync(userId, tier, today, ct);

    public Task<MembershipTier> GetMembershipTierAsync(Guid userId, CancellationToken ct = default) =>
        membershipTiers.GetTierAsync(userId, ct);

    public Task<IReadOnlyDictionary<Guid, MembershipTier>> GetMembershipTiersAsync(CancellationToken ct = default) =>
        membershipTiers.GetTiersAsync(ct);

    public Task<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>> ExpireMembershipTiersAsync(
        MembershipTier tier, LocalDate today, CancellationToken ct = default) =>
        membershipTiers.ExpireAsync(tier, today, ct);

    public Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct) =>
        repository.ReassignApplicationsToUserAsync(sourceUserId, targetUserId, updatedAt, ct);

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

}
