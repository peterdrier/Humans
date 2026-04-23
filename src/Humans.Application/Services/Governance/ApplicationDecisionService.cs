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
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Services.Governance;

/// <summary>
/// Business service for the Governance section. Implements
/// <see cref="IApplicationDecisionService"/> without touching
/// <c>HumansDbContext</c> or <c>IMemoryCache</c> directly — goes through
/// <see cref="IApplicationRepository"/> (persistence),
/// <see cref="IUserService"/> / <see cref="IProfileService"/> (cross-domain
/// reads and writes), and the existing cross-cutting services (audit log,
/// email, notification, team sync, metrics, clock). Nav/notification/voting
/// badge cache invalidations are called inline after successful writes —
/// Governance is low-traffic enough that a caching decorator isn't warranted.
/// </summary>
public sealed class ApplicationDecisionService : IApplicationDecisionService, IUserDataContributor
{
    private readonly IApplicationRepository _repository;
    private readonly IUserService _userService;
    private readonly IProfileService _profileService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IAuditLogService _auditLogService;
    private readonly IEmailService _emailService;
    private readonly INotificationService _notificationService;
    private readonly ISystemTeamSync _syncJob;
    private readonly IHumansMetrics _metrics;
    private readonly INavBadgeCacheInvalidator _navBadge;
    private readonly INotificationMeterCacheInvalidator _notificationMeter;
    private readonly IVotingBadgeCacheInvalidator _votingBadge;
    private readonly IClock _clock;
    private readonly ILogger<ApplicationDecisionService> _logger;

    public ApplicationDecisionService(
        IApplicationRepository repository,
        IUserService userService,
        IProfileService profileService,
        IRoleAssignmentService roleAssignmentService,
        IAuditLogService auditLogService,
        IEmailService emailService,
        INotificationService notificationService,
        ISystemTeamSync syncJob,
        IHumansMetrics metrics,
        INavBadgeCacheInvalidator navBadge,
        INotificationMeterCacheInvalidator notificationMeter,
        IVotingBadgeCacheInvalidator votingBadge,
        IClock clock,
        ILogger<ApplicationDecisionService> logger)
    {
        _repository = repository;
        _userService = userService;
        _profileService = profileService;
        _roleAssignmentService = roleAssignmentService;
        _auditLogService = auditLogService;
        _emailService = emailService;
        _notificationService = notificationService;
        _syncJob = syncJob;
        _metrics = metrics;
        _navBadge = navBadge;
        _notificationMeter = notificationMeter;
        _votingBadge = votingBadge;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ApplicationDecisionResult> ApproveAsync(
        Guid applicationId,
        Guid reviewerUserId,
        string? notes,
        LocalDate? boardMeetingDate,
        CancellationToken cancellationToken = default)
    {
        var application = await _repository.GetByIdAsync(applicationId, cancellationToken);
        if (application is null)
            return new ApplicationDecisionResult(false, "NotFound");

        if (application.Status != ApplicationStatus.Submitted)
            return new ApplicationDecisionResult(false, "NotSubmitted");

        // Capture voter ids BEFORE FinalizeAsync deletes the BoardVote rows as
        // part of its atomic commit — we need them for per-voter VotingBadge
        // invalidation after the write succeeds.
        var voterIds = await _repository.GetVoterIdsForApplicationAsync(applicationId, cancellationToken);

        // State transition — mutates the entity (status, state history row).
        application.Approve(reviewerUserId, notes, _clock);
        application.BoardMeetingDate = boardMeetingDate;
        application.DecisionNote = notes;

        // Term expiry (Dec 31 of next odd year ≥ 2 years out).
        var today = _clock.GetCurrentInstant().InUtc().Date;
        application.TermExpiresAt = TermExpiryCalculator.ComputeTermExpiry(today);

        // Atomic commit of governance-owned state: application update +
        // state history append + board vote bulk-delete in one SaveChanges.
        // MUST happen before audit log / profile tier update / team sync /
        // notifications. Otherwise a repository failure here would leave
        // an orphaned audit entry (append-only per §12, can't be corrected)
        // or a partially-provisioned downstream state claiming an approval
        // that never committed.
        await _repository.FinalizeAsync(application, cancellationToken);

        _navBadge.Invalidate();
        _notificationMeter.Invalidate();
        foreach (var voterId in voterIds)
            _votingBadge.Invalidate(voterId);

        // Audit AFTER the governance state is committed — separate
        // transaction but at minimum correctly sequenced. A failure here
        // leaves a successful application finalize with no audit row,
        // which is recoverable (the state history row on the application
        // itself carries the actor + timestamp).
        await _auditLogService.LogAsync(
            AuditAction.TierApplicationApproved,
            nameof(Humans.Domain.Entities.Application),
            application.Id,
            $"{application.MembershipTier} application approved",
            reviewerUserId);

        _metrics.RecordApplicationProcessed("approved");
        _logger.LogInformation(
            "Application {ApplicationId} approved by {UserId}",
            application.Id, reviewerUserId);

        // Profile tier update — separate transaction. If this fails after
        // FinalizeAsync succeeded, the application is approved but the
        // profile still shows the old tier. Rare and recoverable at
        // single-server scale; documented in the PR.
        await _profileService.SetMembershipTierAsync(
            application.UserId, application.MembershipTier, cancellationToken);

        // Sync team membership for the new tier.
        if (application.MembershipTier == MembershipTier.Colaborador)
            await _syncJob.SyncColaboradorsMembershipForUserAsync(application.UserId, cancellationToken);
        else if (application.MembershipTier == MembershipTier.Asociado)
            await _syncJob.SyncAsociadosMembershipForUserAsync(application.UserId, cancellationToken);

        // Email + in-app notification — best-effort. User info fetched
        // via IUserService now that Application.User is stripped.
        var user = await _userService.GetByIdAsync(application.UserId, cancellationToken);
        if (user is not null)
        {
            try
            {
                await _emailService.SendApplicationApprovedAsync(
                    user.Email ?? string.Empty,
                    user.DisplayName,
                    application.MembershipTier,
                    user.PreferredLanguage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send approval email for {ApplicationId}", application.Id);
            }
        }

        try
        {
            await _notificationService.SendAsync(
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
            _logger.LogError(ex,
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
        var application = await _repository.GetByIdAsync(applicationId, cancellationToken);
        if (application is null)
            return new ApplicationDecisionResult(false, "NotFound");

        if (application.Status != ApplicationStatus.Submitted)
            return new ApplicationDecisionResult(false, "NotSubmitted");

        // Capture voter ids BEFORE FinalizeAsync deletes them (same rationale as ApproveAsync).
        var voterIds = await _repository.GetVoterIdsForApplicationAsync(applicationId, cancellationToken);

        application.Reject(reviewerUserId, reason, _clock);
        application.BoardMeetingDate = boardMeetingDate;
        application.DecisionNote = reason;

        // Atomic commit before audit (same rationale as ApproveAsync).
        await _repository.FinalizeAsync(application, cancellationToken);

        _navBadge.Invalidate();
        _notificationMeter.Invalidate();
        foreach (var voterId in voterIds)
            _votingBadge.Invalidate(voterId);

        await _auditLogService.LogAsync(
            AuditAction.TierApplicationRejected,
            nameof(Humans.Domain.Entities.Application),
            application.Id,
            $"{application.MembershipTier} application rejected",
            reviewerUserId);

        _metrics.RecordApplicationProcessed("rejected");
        _logger.LogInformation(
            "Application {ApplicationId} rejected by {UserId}",
            application.Id, reviewerUserId);

        var user = await _userService.GetByIdAsync(application.UserId, cancellationToken);
        if (user is not null)
        {
            try
            {
                await _emailService.SendApplicationRejectedAsync(
                    user.Email ?? string.Empty,
                    user.DisplayName,
                    application.MembershipTier,
                    reason,
                    user.PreferredLanguage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send rejection email for {ApplicationId}", application.Id);
            }
        }

        try
        {
            await _notificationService.SendAsync(
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
            _logger.LogError(ex,
                "Failed to dispatch ApplicationRejected notification for {ApplicationId}",
                application.Id);
        }

        return new ApplicationDecisionResult(true);
    }

    public async Task<IReadOnlyList<MemberApplication>> GetUserApplicationsAsync(
        Guid userId, CancellationToken ct = default)
    {
        return await _repository.GetByUserIdAsync(userId, ct);
    }

    public async Task<ApplicationUserDetailDto?> GetUserApplicationDetailAsync(
        Guid applicationId, Guid userId, CancellationToken ct = default)
    {
        var application = await _repository.GetByIdAsync(applicationId, ct);
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
        var hasPending = await _repository.AnySubmittedForUserAsync(userId, ct);
        if (hasPending)
            return new ApplicationDecisionResult(false, "AlreadyPending");

        var now = _clock.GetCurrentInstant();
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

        await _repository.AddAsync(application, ct);

        _navBadge.Invalidate();
        _notificationMeter.Invalidate();

        _logger.LogInformation(
            "User {UserId} submitted application {ApplicationId}",
            userId, application.Id);

        // Dispatch in-app notification to Board members — best-effort.
        try
        {
            await _notificationService.SendToRoleAsync(
                NotificationSource.ApplicationSubmitted,
                NotificationClass.Actionable,
                NotificationPriority.Normal,
                $"New {tier} application submitted",
                RoleNames.Board,
                body: $"A new {tier} application requires Board review.",
                actionUrl: "/OnboardingReview/BoardVoting",
                actionLabel: "Review \u2192",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to dispatch ApplicationSubmitted notification for application {ApplicationId}",
                application.Id);
        }

        return new ApplicationDecisionResult(true, ApplicationId: application.Id);
    }

    public async Task<ApplicationDecisionResult> WithdrawAsync(
        Guid applicationId, Guid userId, CancellationToken ct = default)
    {
        var application = await _repository.GetByIdAsync(applicationId, ct);
        if (application is null || application.UserId != userId)
            return new ApplicationDecisionResult(false, "NotFound");

        if (application.Status != ApplicationStatus.Submitted)
            return new ApplicationDecisionResult(false, "CannotWithdraw");

        application.Withdraw(_clock);
        await _repository.UpdateAsync(application, ct);

        _navBadge.Invalidate();
        _notificationMeter.Invalidate();

        _metrics.RecordApplicationProcessed("withdrawn");
        _logger.LogInformation(
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

        var (apps, totalCount) = await _repository.GetFilteredAsync(status, tier, page, pageSize, ct);
        if (apps.Count == 0)
        {
            return (Array.Empty<ApplicationAdminRowDto>(), totalCount);
        }

        var userIds = apps.Select(a => a.UserId).Distinct().ToList();
        var users = await _userService.GetByIdsAsync(userIds, ct);

        var rows = apps.Select(a =>
        {
            var user = users.GetValueOrDefault(a.UserId);
            return new ApplicationAdminRowDto(
                Id: a.Id,
                UserId: a.UserId,
                UserEmail: user?.Email ?? string.Empty,
                UserDisplayName: user?.DisplayName ?? string.Empty,
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
        var application = await _repository.GetByIdAsync(applicationId, ct);
        if (application is null)
            return null;

        // Collect every user id we need in one bulk fetch: applicant,
        // reviewer (if any), plus every state-history actor.
        var userIds = new HashSet<Guid> { application.UserId };
        if (application.ReviewedByUserId is { } reviewerId)
            userIds.Add(reviewerId);
        foreach (var row in application.StateHistory)
            userIds.Add(row.ChangedByUserId);

        var users = await _userService.GetByIdsAsync(userIds, ct);

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
                ChangedByDisplayName: users.GetValueOrDefault(h.ChangedByUserId)?.DisplayName,
                Notes: h.Notes))
            .ToList();

        return new ApplicationAdminDetailDto(
            Id: application.Id,
            UserId: application.UserId,
            UserEmail: applicant?.Email ?? string.Empty,
            UserDisplayName: applicant?.DisplayName ?? string.Empty,
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
            ReviewerName: reviewer?.DisplayName,
            ReviewNotes: application.ReviewNotes,
            History: history);
    }

    // ==========================================================================
    // Onboarding-section support methods — Governance owns application and
    // board-vote tables (design-rules §2c), so these cross-section queries
    // live here and are consumed by OnboardingService.
    // ==========================================================================

    public Task<IReadOnlySet<Guid>> GetUserIdsWithPendingApplicationAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        _repository.GetUserIdsWithSubmittedAsync(userIds, ct);

    public Task<MemberApplication?> GetSubmittedApplicationForUserAsync(
        Guid userId, CancellationToken ct = default) =>
        _repository.GetSubmittedForUserAsync(userId, ct);

    public Task<IReadOnlyList<MembershipTier>> GetApprovedTiersForUserAsync(
        Guid userId, CancellationToken ct = default) =>
        _repository.GetApprovedTiersForUserAsync(userId, ct);

    public async Task<BoardVotingDashboardData> GetBoardVotingDashboardAsync(
        CancellationToken ct = default)
    {
        var applications = await _repository.GetAllSubmittedWithVotesAsync(ct);

        // Stitch applicant display info in memory (cross-domain nav stripped).
        var applicantIds = applications.Select(a => a.UserId).Distinct().ToList();
        var applicantsById = await _userService.GetByIdsAsync(applicantIds, ct);

        var rows = applications.Select(a =>
        {
            var applicant = applicantsById.GetValueOrDefault(a.UserId);
            return new BoardVotingDashboardRow(
                ApplicationId: a.Id,
                UserId: a.UserId,
                UserDisplayName: applicant?.DisplayName ?? string.Empty,
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

        var boardMemberIds = await _roleAssignmentService.GetActiveUserIdsInRoleAsync(
            RoleNames.Board, ct);
        var boardUsersById = await _userService.GetByIdsAsync(boardMemberIds, ct);
        var boardMembers = boardUsersById.Values
            .Select(u => new BoardMemberInfo(u.Id, u.DisplayName))
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BoardVotingDashboardData(rows, boardMembers);
    }

    public async Task<BoardVotingDetailData?> GetBoardVotingDetailAsync(
        Guid applicationId, CancellationToken ct = default)
    {
        var application = await _repository.GetByIdAsync(applicationId, ct);
        if (application is null)
            return null;

        var applicant = await _userService.GetByIdAsync(application.UserId, ct);
        var profile = await _profileService.GetProfileAsync(application.UserId, ct);

        var voterIds = application.BoardVotes
            .Select(v => v.BoardMemberUserId)
            .Distinct()
            .ToList();
        var votersById = await _userService.GetByIdsAsync(voterIds, ct);

        var voteRows = application.BoardVotes
            .Select(v => new BoardVoteRow(
                BoardMemberUserId: v.BoardMemberUserId,
                BoardMemberDisplayName: votersById.GetValueOrDefault(v.BoardMemberUserId)?.DisplayName,
                Vote: v.Vote,
                Note: v.Note,
                VotedAt: v.VotedAt))
            .ToList();

        return new BoardVotingDetailData(
            ApplicationId: application.Id,
            UserId: application.UserId,
            DisplayName: applicant?.DisplayName ?? string.Empty,
            ProfilePictureUrl: applicant?.ProfilePictureUrl,
            Email: applicant?.Email ?? string.Empty,
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
        _repository.HasBoardVotesAsync(applicationId, ct);

    public async Task<ApplicationDecisionResult> CastBoardVoteAsync(
        Guid applicationId,
        Guid boardMemberUserId,
        VoteChoice vote,
        string? note,
        CancellationToken ct = default)
    {
        var application = await _repository.GetByIdAsync(applicationId, ct);
        if (application is null)
            return new ApplicationDecisionResult(false, "NotFound");

        if (application.Status != ApplicationStatus.Submitted)
            return new ApplicationDecisionResult(false, "NotSubmitted");

        var now = _clock.GetCurrentInstant();
        await _repository.UpsertBoardVoteAsync(applicationId, boardMemberUserId, vote, note, now, ct);

        _votingBadge.Invalidate(boardMemberUserId);

        _logger.LogInformation(
            "Board member {UserId} voted {Vote} on application {ApplicationId}",
            boardMemberUserId, vote, applicationId);

        return new ApplicationDecisionResult(true);
    }

    public Task<int> GetUnvotedApplicationCountAsync(
        Guid boardMemberUserId, CancellationToken ct = default) =>
        _repository.GetUnvotedCountForBoardMemberAsync(boardMemberUserId, ct);

    public Task<ApplicationAdminStats> GetAdminStatsAsync(CancellationToken ct = default) =>
        _repository.GetAdminStatsAsync(ct);

    public Task<int> GetPendingApplicationCountAsync(CancellationToken ct = default) =>
        _repository.CountByStatusAsync(ApplicationStatus.Submitted, ct);

    public async Task UpdateDraftApplicationAsync(
        Guid applicationId, MembershipTier tier, string motivation,
        string? additionalInfo, string? significantContribution, string? roleUnderstanding,
        CancellationToken ct = default)
    {
        var application = await _repository.GetByIdAsync(applicationId, ct);
        if (application is null)
        {
            _logger.LogWarning("UpdateDraftApplicationAsync: application {Id} not found", applicationId);
            return;
        }

        if (application.Status != ApplicationStatus.Submitted)
        {
            _logger.LogWarning(
                "UpdateDraftApplicationAsync: application {Id} is {Status}, not Submitted",
                applicationId, application.Status);
            return;
        }

        application.MembershipTier = tier;
        application.Motivation = motivation;
        application.AdditionalInfo = additionalInfo;
        application.SignificantContribution = significantContribution;
        application.RoleUnderstanding = roleUnderstanding;
        application.UpdatedAt = _clock.GetCurrentInstant();

        await _repository.UpdateAsync(application, ct);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var applications = await _repository.GetByUserIdAsync(userId, ct);

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

        IReadOnlyDictionary<Guid, User> users = userIds.Count == 0
            ? new Dictionary<Guid, User>()
            : await _userService.GetByIdsAsync(userIds, ct);

        var reviewerName = application.ReviewedByUserId is { } rid
            ? users.GetValueOrDefault(rid)?.DisplayName
            : null;

        var history = application.StateHistory
            .OrderByDescending(h => h.ChangedAt)
            .Select(h => new ApplicationStateHistoryDto(
                Status: h.Status,
                ChangedAt: h.ChangedAt,
                ChangedByUserId: h.ChangedByUserId,
                ChangedByDisplayName: users.GetValueOrDefault(h.ChangedByUserId)?.DisplayName,
                Notes: h.Notes))
            .ToList();

        return (reviewerName, history);
    }
}
