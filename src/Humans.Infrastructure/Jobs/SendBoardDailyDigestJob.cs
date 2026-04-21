using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

// RoleAssignment.User cross-domain nav is [Obsolete] (design-rules §6c). This job
// still reads RoleAssignments directly; migration to IRoleAssignmentService +
// IUserService.GetByIdsAsync is tracked as a §15h touch-and-clean follow-up.
#pragma warning disable CS0618

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Nightly job that emails each Board member a summary of the previous UTC day's approvals
/// plus outstanding items requiring attention.
/// </summary>
public class SendBoardDailyDigestJob : IRecurringJob
{
    private readonly HumansDbContext _dbContext;
    private readonly IEmailService _emailService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IHumansMetrics _metrics;
    private readonly ILogger<SendBoardDailyDigestJob> _logger;
    private readonly IClock _clock;

    public SendBoardDailyDigestJob(
        HumansDbContext dbContext,
        IEmailService emailService,
        IMembershipCalculator membershipCalculator,
        IHumansMetrics metrics,
        ILogger<SendBoardDailyDigestJob> logger,
        IClock clock)
    {
        _dbContext = dbContext;
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

            // 1. Volunteer approvals — from audit log (VolunteerApproved action)
            var volunteerUserIds = await _dbContext.AuditLogEntries
                .Where(e => e.Action == AuditAction.VolunteerApproved
                    && e.OccurredAt >= windowStart
                    && e.OccurredAt < windowEnd)
                .Select(e => e.EntityId)
                .Distinct()
                .ToListAsync(cancellationToken);

            if (volunteerUserIds.Count > 0)
            {
                var volunteerNames = await _dbContext.Users
                    .Where(u => volunteerUserIds.Contains(u.Id))
                    .Select(u => u.DisplayName)
                    .OrderBy(n => n)
                    .ToListAsync(cancellationToken);

                groups.Add(new BoardDigestTierGroup("Volunteer", volunteerNames));
            }

            // 2. Tier application approvals (Colaborador/Asociado).
            //    Application.User nav was stripped in the Governance migration —
            //    fetch applicants separately and stitch DisplayName in memory.
            var tierApprovals = await _dbContext.Applications
                .Where(a => a.Status == ApplicationStatus.Approved
                    && a.ResolvedAt != null
                    && a.ResolvedAt.Value >= windowStart
                    && a.ResolvedAt.Value < windowEnd)
                .Select(a => new { a.MembershipTier, a.UserId })
                .ToListAsync(cancellationToken);

            if (tierApprovals.Count > 0)
            {
                var approvedUserIds = tierApprovals
                    .Select(a => a.UserId)
                    .Distinct()
                    .ToList();
                var approvedUsersById = await _dbContext.Users
                    .AsNoTracking()
                    .Where(u => approvedUserIds.Contains(u.Id))
                    .Select(u => new { u.Id, u.DisplayName })
                    .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);

                foreach (var tierGroup in tierApprovals
                    .GroupBy(a => a.MembershipTier)
                    .OrderBy(g => g.Key))
                {
                    var tierLabel = tierGroup.Key.ToString();
                    var names = tierGroup
                        .Select(a => approvedUsersById.GetValueOrDefault(a.UserId) ?? string.Empty)
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToList();
                    groups.Add(new BoardDigestTierGroup(tierLabel, names));
                }
            }

            // 3. Compute shared outstanding counts
            var onboardingReviewCount = await _dbContext.Profiles
                .CountAsync(p => p.ConsentCheckStatus != null
                    && (p.ConsentCheckStatus == ConsentCheckStatus.Pending || p.ConsentCheckStatus == ConsentCheckStatus.Flagged)
                    && p.RejectedAt == null, cancellationToken);

            var totalNotApproved = await _dbContext.Profiles
                .CountAsync(p => !p.IsApproved && !p.IsSuspended, cancellationToken);
            var stillOnboardingCount = totalNotApproved - onboardingReviewCount;
            if (stillOnboardingCount < 0) stillOnboardingCount = 0;

            var boardVotingTotal = await _dbContext.Applications
                .CountAsync(a => a.Status == ApplicationStatus.Submitted, cancellationToken);

            var teamJoinRequestCount = await _dbContext.TeamJoinRequests
                .CountAsync(r => r.Status == TeamJoinRequestStatus.Pending, cancellationToken);

            // Pending consents (same logic as AdminController dashboard)
            var allUserIds = await _dbContext.Users.Select(u => u.Id).ToListAsync(cancellationToken);
            var usersWithAllConsents = await _membershipCalculator.GetUsersWithAllRequiredConsentsAsync(allUserIds, cancellationToken);

            var leadUserIds = await _dbContext.TeamMembers
                .Where(tm => tm.LeftAt == null && tm.Role == TeamMemberRole.Coordinator && tm.Team.SystemTeamType == SystemTeamType.None)
                .Select(tm => tm.UserId)
                .Distinct()
                .ToListAsync(cancellationToken);
            var leadsWithAllConsents = leadUserIds.Count > 0
                ? await _membershipCalculator.GetUsersWithAllRequiredConsentsForTeamAsync(leadUserIds, SystemTeamIds.Coordinators, cancellationToken)
                : new HashSet<Guid>();

            var pendingConsentsCount = allUserIds.Count(id =>
                !usersWithAllConsents.Contains(id) ||
                (leadUserIds.Contains(id) && !leadsWithAllConsents.Contains(id)));

            var pendingDeletionsCount = await _dbContext.Users
                .CountAsync(u => u.DeletionRequestedAt != null, cancellationToken);

            // Only skip if both no approvals AND all counts are zero
            var hasOutstandingItems = onboardingReviewCount > 0 || stillOnboardingCount > 0
                || boardVotingTotal > 0 || teamJoinRequestCount > 0
                || pendingConsentsCount > 0 || pendingDeletionsCount > 0;

            if (groups.Count == 0 && !hasOutstandingItems)
            {
                _logger.LogInformation("No approvals and no outstanding items on {Date}, skipping Board digest", dateLabel);
                _metrics.RecordJobRun("board_daily_digest", "skipped");
                return;
            }

            // 4. Submitted application IDs (for per-member vote count)
            var submittedApplicationIds = boardVotingTotal > 0
                ? await _dbContext.Applications
                    .Where(a => a.Status == ApplicationStatus.Submitted)
                    .Select(a => a.Id)
                    .ToListAsync(cancellationToken)
                : new List<Guid>();

            // 5. Find active Board members
            var boardMembers = await _dbContext.RoleAssignments
                .Include(ra => ra.User)
                    .ThenInclude(u => u.UserEmails)
                .Where(ra => ra.RoleName == RoleNames.Board
                    && ra.ValidFrom <= now
                    && (ra.ValidTo == null || ra.ValidTo.Value > now))
                .Select(ra => ra.User)
                .Distinct()
                .ToListAsync(cancellationToken);

            var sentCount = 0;
            foreach (var member in boardMembers)
            {
                var email = member.GetEffectiveEmail();
                if (email is null)
                {
                    _logger.LogWarning("Board member {UserId} ({Name}) has no effective email, skipping digest",
                        member.Id, member.DisplayName);
                    continue;
                }

                // Per-member: how many submitted applications this member hasn't voted on
                var boardVotingYours = 0;
                if (submittedApplicationIds.Count > 0)
                {
                    var votedAppIds = await _dbContext.BoardVotes
                        .Where(v => v.BoardMemberUserId == member.Id
                            && submittedApplicationIds.Contains(v.ApplicationId))
                        .Select(v => v.ApplicationId)
                        .ToListAsync(cancellationToken);

                    boardVotingYours = submittedApplicationIds.Count - votedAppIds.Count;
                }

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
