using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Daily job that emails each Admin a digest of system health and pending actions.
/// </summary>
public class SendAdminDailyDigestJob : IRecurringJob
{
    private readonly HumansDbContext _dbContext;
    private readonly IEmailService _emailService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly HumansMetricsService _metrics;
    private readonly ILogger<SendAdminDailyDigestJob> _logger;
    private readonly IClock _clock;

    public SendAdminDailyDigestJob(
        HumansDbContext dbContext,
        IEmailService emailService,
        IMembershipCalculator membershipCalculator,
        HumansMetricsService metrics,
        ILogger<SendAdminDailyDigestJob> logger,
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
        var dateLabel = todayUtc.ToIsoDateString();

        _logger.LogInformation("Starting Admin daily digest job for {Date}", dateLabel);

        try
        {
            // Pending deletions
            var pendingDeletionsCount = await _dbContext.Users
                .CountAsync(u => u.DeletionRequestedAt != null, cancellationToken);

            // Pending consents
            var allUserIds = await _dbContext.Users.Select(u => u.Id).ToListAsync(cancellationToken);
            var usersWithAllConsents = await _membershipCalculator.GetUsersWithAllRequiredConsentsAsync(allUserIds, cancellationToken);

            var leadUserIds = await _dbContext.TeamMembers
                .Where(tm => tm.LeftAt == null && tm.Role == TeamMemberRole.Coordinator && tm.Team.SystemTeamType == SystemTeamType.None)
                .Select(tm => tm.UserId)
                .Distinct()
                .ToListAsync(cancellationToken);
            var leadsWithAllConsents = leadUserIds.Count > 0
                ? await _membershipCalculator.GetUsersWithAllRequiredConsentsForTeamAsync(leadUserIds, SystemTeamIds.Coordinators, cancellationToken)
                : (IReadOnlySet<Guid>)new HashSet<Guid>();

            var pendingConsentsCount = allUserIds.Count(id =>
                !usersWithAllConsents.Contains(id) ||
                (leadUserIds.Contains(id) && !leadsWithAllConsents.Contains(id)));

            // Team join requests
            var teamJoinRequestCount = await _dbContext.TeamJoinRequests
                .CountAsync(r => r.Status == TeamJoinRequestStatus.Pending, cancellationToken);

            // Onboarding review
            var onboardingReviewCount = await _dbContext.Profiles
                .CountAsync(p => p.ConsentCheckStatus != null
                    && (p.ConsentCheckStatus == ConsentCheckStatus.Pending || p.ConsentCheckStatus == ConsentCheckStatus.Flagged)
                    && p.RejectedAt == null, cancellationToken);

            var totalNotApproved = await _dbContext.Profiles
                .CountAsync(p => !p.IsApproved && !p.IsSuspended, cancellationToken);
            var stillOnboardingCount = Math.Max(0, totalNotApproved - onboardingReviewCount);

            // Board voting
            var boardVotingTotal = await _dbContext.Applications
                .CountAsync(a => a.Status == ApplicationStatus.Submitted, cancellationToken);

            // Stale Google sync outbox events (unprocessed with errors, excluding permanent failures)
            var failedSyncEvents = await _dbContext.GoogleSyncOutboxEvents
                .CountAsync(e => e.ProcessedAt == null && e.LastError != null && !e.FailedPermanently, cancellationToken);

            // Permanent Google sync failures — count distinct users with rejected email status
            var permanentSyncFailures = await _dbContext.Users
                .CountAsync(u => u.GoogleEmailStatus == GoogleEmailStatus.Rejected, cancellationToken);

            // Transient retries (still being retried, have errors but not permanent)
            var transientSyncRetries = await _dbContext.GoogleSyncOutboxEvents
                .CountAsync(e => e.ProcessedAt == null && !e.FailedPermanently && e.RetryCount > 0, cancellationToken);

            // Ticket sync status
            var ticketSyncState = await _dbContext.TicketSyncStates.FindAsync([1], cancellationToken);
            var ticketSyncError = ticketSyncState?.SyncStatus == TicketSyncStatus.Error;
            var ticketSyncErrorMessage = ticketSyncError ? ticketSyncState?.LastError : null;

            var counts = new AdminDigestCounts(
                pendingDeletionsCount,
                pendingConsentsCount,
                teamJoinRequestCount,
                onboardingReviewCount,
                stillOnboardingCount,
                boardVotingTotal,
                failedSyncEvents,
                permanentSyncFailures,
                transientSyncRetries,
                ticketSyncError,
                ticketSyncErrorMessage);

            // Skip if everything is clear
            var hasItems = pendingDeletionsCount > 0 || pendingConsentsCount > 0
                || teamJoinRequestCount > 0 || onboardingReviewCount > 0
                || stillOnboardingCount > 0 || boardVotingTotal > 0
                || failedSyncEvents > 0 || permanentSyncFailures > 0
                || transientSyncRetries > 0 || ticketSyncError;

            if (!hasItems)
            {
                _logger.LogInformation("No pending items on {Date}, skipping Admin digest", dateLabel);
                _metrics.RecordJobRun("admin_daily_digest", "skipped");
                return;
            }

            // Find active Admins
            var admins = await _dbContext.RoleAssignments
                .Include(ra => ra.User)
                    .ThenInclude(u => u.UserEmails)
                .Where(ra => ra.RoleName == RoleNames.Admin
                    && ra.ValidFrom <= now
                    && (ra.ValidTo == null || ra.ValidTo.Value > now))
                .Select(ra => ra.User)
                .Distinct()
                .ToListAsync(cancellationToken);

            var sentCount = 0;
            foreach (var admin in admins)
            {
                var email = admin.GetEffectiveEmail();
                if (email is null)
                {
                    _logger.LogWarning("Admin {UserId} ({Name}) has no effective email, skipping digest",
                        admin.Id, admin.DisplayName);
                    continue;
                }

                await _emailService.SendAdminDailyDigestAsync(
                    email, admin.DisplayName, dateLabel, counts,
                    admin.PreferredLanguage, cancellationToken);
                sentCount++;
            }

            _metrics.RecordJobRun("admin_daily_digest", "success");
            _logger.LogInformation(
                "Admin daily digest sent to {Count} admins for {Date}",
                sentCount, dateLabel);
        }
        catch (Exception ex)
        {
            _metrics.RecordJobRun("admin_daily_digest", "failure");
            _logger.LogError(ex, "Error sending Admin daily digest for {Date}", dateLabel);
            throw;
        }
    }
}
