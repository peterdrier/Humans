using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Nightly job that emails each Board member a summary of the previous UTC
/// day's approvals plus outstanding items requiring attention.
/// </summary>
/// <remarks>
/// All reads fan out through section services
/// (<see cref="IAuditLogService"/>, <see cref="IApplicationDecisionService"/>,
/// <see cref="IUserService"/>, <see cref="IProfileService"/>,
/// <see cref="ITeamService"/>, <see cref="IRoleAssignmentService"/>) so the
/// job never touches <see cref="Humans.Infrastructure.Data.HumansDbContext"/>
/// directly (design-rules §2c).
/// </remarks>
public class SendBoardDailyDigestJob : IRecurringJob
{
    private readonly IAuditLogService _auditLogService;
    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly IUserService _userService;
    private readonly IProfileService _profileService;
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IEmailService _emailService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IJobRunMetrics _metrics;
    private readonly ILogger<SendBoardDailyDigestJob> _logger;
    private readonly IClock _clock;

    public SendBoardDailyDigestJob(
        IAuditLogService auditLogService,
        IApplicationDecisionService applicationDecisionService,
        IUserService userService,
        IProfileService profileService,
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService,
        IEmailService emailService,
        IMembershipCalculator membershipCalculator,
        IJobRunMetrics metrics,
        ILogger<SendBoardDailyDigestJob> logger,
        IClock clock)
    {
        _auditLogService = auditLogService;
        _applicationDecisionService = applicationDecisionService;
        _userService = userService;
        _profileService = profileService;
        _teamService = teamService;
        _roleAssignmentService = roleAssignmentService;
        _emailService = emailService;
        _membershipCalculator = membershipCalculator;
        _metrics = metrics;
        _logger = logger;
        _clock = clock;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var todayUtc = now.InUtc().Date;
        var yesterdayUtc = todayUtc.PlusDays(-1);
        var windowStart = yesterdayUtc.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
        var windowEnd = todayUtc.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
        var dateLabel = yesterdayUtc.ToIsoDateString();

        _logger.LogInformation(
            "Starting Board daily digest job for {Date} (window {Start} to {End})",
            dateLabel, windowStart, windowEnd);

        try
        {
            var groups = new List<BoardDigestTierGroup>();

            // 1. Volunteer approvals — routed via IAuditLogService.
            var volunteerUserIds = await _auditLogService.GetEntityIdsForActionInWindowAsync(
                windowStart, windowEnd, AuditAction.VolunteerApproved, cancellationToken);

            if (volunteerUserIds.Count > 0)
            {
                var volunteerUsers = await _userService.GetByIdsAsync(volunteerUserIds, cancellationToken);
                var volunteerNames = volunteerUsers.Values
                    .Select(u => u.DisplayName)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();

                groups.Add(new BoardDigestTierGroup("Volunteer", volunteerNames));
            }

            // 2. Tier application approvals (Colaborador/Asociado) — routed via
            //    IApplicationDecisionService.
            var tierApprovals = await _applicationDecisionService.GetApprovedInWindowAsync(
                windowStart, windowEnd, cancellationToken);

            if (tierApprovals.Count > 0)
            {
                var approvedUserIds = tierApprovals
                    .Select(a => a.UserId)
                    .Distinct()
                    .ToList();
                var approvedUsersById = await _userService.GetByIdsAsync(approvedUserIds, cancellationToken);

                foreach (var tierGroup in tierApprovals
                    .GroupBy(a => a.MembershipTier)
                    .OrderBy(g => g.Key))
                {
                    var tierLabel = tierGroup.Key.ToString();
                    var names = tierGroup
                        .Select(a => approvedUsersById.TryGetValue(a.UserId, out var u) ? u.DisplayName : string.Empty)
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToList();
                    groups.Add(new BoardDigestTierGroup(tierLabel, names));
                }
            }

            // 3. Compute shared outstanding counts via owning services.
            var onboardingReviewCount = await _profileService.GetConsentReviewPendingCountAsync(cancellationToken);
            var totalNotApproved = await _profileService.GetNotApprovedAndNotSuspendedCountAsync(cancellationToken);
            var stillOnboardingCount = totalNotApproved - onboardingReviewCount;
            if (stillOnboardingCount < 0) stillOnboardingCount = 0;

            var boardVotingTotal = await _applicationDecisionService.GetPendingApplicationCountAsync(cancellationToken);
            var teamJoinRequestCount = await _teamService.GetTotalPendingJoinRequestCountAsync(cancellationToken);

            // Pending consents (same logic as Admin digest).
            var allUserIds = await _userService.GetAllUserIdsAsync(cancellationToken);
            var allUserIdsList = allUserIds.ToList();
            var usersWithAllConsents = await _membershipCalculator
                .GetUsersWithAllRequiredConsentsAsync(allUserIdsList, cancellationToken);

            var leadUserIds = await _teamService.GetActiveNonSystemTeamCoordinatorUserIdsAsync(cancellationToken);
            var leadUserIdsList = leadUserIds.ToList();
            var leadsWithAllConsents = leadUserIdsList.Count > 0
                ? await _membershipCalculator.GetUsersWithAllRequiredConsentsForTeamAsync(
                    leadUserIdsList, SystemTeamIds.Coordinators, cancellationToken)
                : new HashSet<Guid>();
            var leadSet = leadUserIdsList.ToHashSet();

            var pendingConsentsCount = allUserIdsList.Count(id =>
                !usersWithAllConsents.Contains(id) ||
                (leadSet.Contains(id) && !leadsWithAllConsents.Contains(id)));

            var pendingDeletionsCount = await _userService.GetPendingDeletionCountAsync(cancellationToken);

            // Only skip if both no approvals AND all counts are zero.
            var hasOutstandingItems = onboardingReviewCount > 0 || stillOnboardingCount > 0
                || boardVotingTotal > 0 || teamJoinRequestCount > 0
                || pendingConsentsCount > 0 || pendingDeletionsCount > 0;

            if (groups.Count == 0 && !hasOutstandingItems)
            {
                _logger.LogInformation(
                    "No approvals and no outstanding items on {Date}, skipping Board digest",
                    dateLabel);
                _metrics.RecordJobRun("board_daily_digest", "skipped");
                return;
            }

            // 4. Submitted application ids (for per-member vote counts).
            var submittedApplicationIds = boardVotingTotal > 0
                ? await _applicationDecisionService.GetSubmittedApplicationIdsAsync(cancellationToken)
                : (IReadOnlyList<Guid>)Array.Empty<Guid>();

            // 5. Resolve active Board members via the role-assignment service.
            // Include UserEmails so GetEffectiveEmail picks the verified
            // notification-target address instead of silently falling back
            // to User.Email.
            var boardUserIds = await _roleAssignmentService.GetActiveUserIdsInRoleAsync(
                RoleNames.Board, cancellationToken);
            var boardMembers = await _userService.GetByIdsWithEmailsAsync(boardUserIds, cancellationToken);

            var sentCount = 0;
            foreach (var member in boardMembers.Values)
            {
                var email = member.GetEffectiveEmail();
                if (email is null)
                {
                    _logger.LogWarning(
                        "Board member {UserId} ({Name}) has no effective email, skipping digest",
                        member.Id, member.DisplayName);
                    continue;
                }

                // Per-member: how many submitted applications this member
                // hasn't voted on.
                var boardVotingYours = submittedApplicationIds.Count > 0
                    ? await _applicationDecisionService.GetUnvotedCountForBoardMemberAmongApplicationsAsync(
                        member.Id, submittedApplicationIds, cancellationToken)
                    : 0;

                var counts = new BoardDigestOutstandingCounts(
                    onboardingReviewCount,
                    stillOnboardingCount,
                    boardVotingTotal,
                    boardVotingYours,
                    teamJoinRequestCount,
                    pendingConsentsCount,
                    pendingDeletionsCount);

                await _emailService.SendBoardDailyDigestAsync(
                    email, member.DisplayName, dateLabel, groups, counts,
                    member.PreferredLanguage, cancellationToken);
                sentCount++;
            }

            _metrics.RecordJobRun("board_daily_digest", "success");
            _logger.LogInformation(
                "Board daily digest sent to {Count} Board members for {Date} ({TierCount} tier groups, {TotalApprovals} approvals, outstanding: {Outstanding})",
                sentCount, dateLabel, groups.Count, groups.Sum(g => g.DisplayNames.Count), hasOutstandingItems);
        }
        catch (Exception ex)
        {
            _metrics.RecordJobRun("board_daily_digest", "failure");
            _logger.LogError(ex, "Error sending Board daily digest for {Date}", dateLabel);
            throw;
        }
    }
}
