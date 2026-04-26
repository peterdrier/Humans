using Hangfire;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Daily job that emails each Admin a digest of system health and pending actions.
/// </summary>
/// <remarks>
/// All reads fan out through section services
/// (<see cref="IUserService"/>, <see cref="IProfileService"/>,
/// <see cref="ITeamService"/>, <see cref="IApplicationDecisionService"/>,
/// <see cref="IRoleAssignmentService"/>, <see cref="ITicketSyncService"/>,
/// <see cref="IGoogleSyncOutboxRepository"/>) so the job never touches
/// <see cref="Humans.Infrastructure.Data.HumansDbContext"/> directly
/// (design-rules §2c).
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class SendAdminDailyDigestJob : IRecurringJob
{
    private readonly IUserService _userService;
    private readonly IProfileService _profileService;
    private readonly ITeamService _teamService;
    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly ITicketSyncService _ticketSyncService;
    private readonly IGoogleSyncOutboxRepository _googleSyncOutboxRepository;
    private readonly IEmailService _emailService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IHumansMetrics _metrics;
    private readonly ILogger<SendAdminDailyDigestJob> _logger;
    private readonly IClock _clock;

    public SendAdminDailyDigestJob(
        IUserService userService,
        IProfileService profileService,
        ITeamService teamService,
        IApplicationDecisionService applicationDecisionService,
        IRoleAssignmentService roleAssignmentService,
        ITicketSyncService ticketSyncService,
        IGoogleSyncOutboxRepository googleSyncOutboxRepository,
        IEmailService emailService,
        IMembershipCalculator membershipCalculator,
        IHumansMetrics metrics,
        ILogger<SendAdminDailyDigestJob> logger,
        IClock clock)
    {
        _userService = userService;
        _profileService = profileService;
        _teamService = teamService;
        _applicationDecisionService = applicationDecisionService;
        _roleAssignmentService = roleAssignmentService;
        _ticketSyncService = ticketSyncService;
        _googleSyncOutboxRepository = googleSyncOutboxRepository;
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
            // Pending deletions / consent reviews / onboarding / voting.
            var pendingDeletionsCount = await _userService.GetPendingDeletionCountAsync(cancellationToken);
            var onboardingReviewCount = await _profileService.GetConsentReviewPendingCountAsync(cancellationToken);
            var totalNotApproved = await _profileService.GetNotApprovedAndNotSuspendedCountAsync(cancellationToken);
            var stillOnboardingCount = Math.Max(0, totalNotApproved - onboardingReviewCount);
            var boardVotingTotal = await _applicationDecisionService.GetPendingApplicationCountAsync(cancellationToken);
            var teamJoinRequestCount = await _teamService.GetTotalPendingJoinRequestCountAsync(cancellationToken);

            // Pending consents calculation — reuses the same inputs the Admin
            // dashboard does so the numbers match.
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

            // Google sync outbox counts — already routed through the repo.
            var failedSyncEvents = await _googleSyncOutboxRepository.CountStaleAsync(cancellationToken);
            var transientSyncRetries = await _googleSyncOutboxRepository.CountTransientRetriesAsync(cancellationToken);
            var permanentSyncFailures = await _userService.GetRejectedGoogleEmailCountAsync(cancellationToken);

            // Ticket sync status — routed through ITicketSyncService.
            var ticketSyncStatus = await _ticketSyncService.GetErrorStatusAsync(cancellationToken);

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
                ticketSyncStatus.InError,
                ticketSyncStatus.ErrorMessage);

            // Skip if everything is clear
            var hasItems = pendingDeletionsCount > 0 || pendingConsentsCount > 0
                || teamJoinRequestCount > 0 || onboardingReviewCount > 0
                || stillOnboardingCount > 0 || boardVotingTotal > 0
                || failedSyncEvents > 0 || permanentSyncFailures > 0
                || transientSyncRetries > 0 || ticketSyncStatus.InError;

            if (!hasItems)
            {
                _logger.LogInformation("No pending items on {Date}, skipping Admin digest", dateLabel);
                _metrics.RecordJobRun("admin_daily_digest", "skipped");
                return;
            }

            // Resolve active Admin users via the role-assignment service.
            // Include UserEmails so GetEffectiveEmail picks the verified
            // notification-target address instead of silently falling back
            // to User.Email.
            var adminUserIds = await _roleAssignmentService.GetActiveUserIdsInRoleAsync(
                RoleNames.Admin, cancellationToken);
            var admins = await _userService.GetByIdsWithEmailsAsync(adminUserIds, cancellationToken);

            var sentCount = 0;
            foreach (var admin in admins.Values)
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
