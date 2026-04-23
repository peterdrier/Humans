using System.Globalization;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Nightly job that reminds approved members whose Colaborador / Asociado
/// term expires within 90 days to submit a renewal application.
/// </summary>
/// <remarks>
/// Reads Applications and marks <c>RenewalReminderSentAt</c> through
/// <see cref="IApplicationDecisionService"/>, and stitches applicant display
/// info via <see cref="IUserService"/>, so the job never touches
/// <see cref="Humans.Infrastructure.Data.HumansDbContext"/> directly
/// (design-rules §2c).
/// </remarks>
public class TermRenewalReminderJob : IRecurringJob
{
    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly IUserService _userService;
    private readonly IEmailService _emailService;
    private readonly INotificationService _notificationService;
    private readonly IHumansMetrics _metrics;
    private readonly ILogger<TermRenewalReminderJob> _logger;
    private readonly IClock _clock;

    private const int ReminderDaysBeforeExpiry = 90;

    public TermRenewalReminderJob(
        IApplicationDecisionService applicationDecisionService,
        IUserService userService,
        IEmailService emailService,
        INotificationService notificationService,
        IHumansMetrics metrics,
        ILogger<TermRenewalReminderJob> logger,
        IClock clock)
    {
        _applicationDecisionService = applicationDecisionService;
        _userService = userService;
        _emailService = emailService;
        _notificationService = notificationService;
        _metrics = metrics;
        _logger = logger;
        _clock = clock;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting term renewal reminder job at {Time}", _clock.GetCurrentInstant());

        try
        {
            var today = _clock.GetCurrentInstant().InUtc().Date;
            var reminderThreshold = today.PlusDays(ReminderDaysBeforeExpiry);

            // Find approved applications with term expiring within 90 days that
            // haven't had a reminder sent yet.
            var expiringApplications = await _applicationDecisionService
                .GetExpiringApplicationsNeedingReminderAsync(today, reminderThreshold, cancellationToken);

            // For each user, only consider the latest approved application per
            // tier (a user might have multiple approved applications if they
            // renewed before).
            var latestPerUserTier = expiringApplications
                .GroupBy(a => new { a.UserId, a.MembershipTier })
                .Select(g => g.OrderByDescending(a => a.SubmittedAt).First())
                .ToList();

            // Exclude users who already have a pending renewal application for
            // the same tier.
            var pendingSet = await _applicationDecisionService
                .GetPendingApplicationUserTiersAsync(cancellationToken);

            // Stitch applicant user info in memory — Application.User
            // cross-domain nav was stripped in the Governance migration
            // (design-rules §6).
            var applicantIds = latestPerUserTier
                .Select(a => a.UserId)
                .Distinct()
                .ToList();
            var applicantsById = applicantIds.Count == 0
                ? new Dictionary<Guid, Domain.Entities.User>()
                : (await _userService.GetByIdsAsync(applicantIds, cancellationToken))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

            var sentCount = 0;
            var now = _clock.GetCurrentInstant();

            foreach (var application in latestPerUserTier)
            {
                if (pendingSet.Contains((application.UserId, application.MembershipTier)))
                {
                    _logger.LogDebug(
                        "Skipping renewal reminder for user {UserId} tier {Tier} — pending application exists",
                        application.UserId, application.MembershipTier);
                    continue;
                }

                if (!applicantsById.TryGetValue(application.UserId, out var applicant))
                {
                    _logger.LogWarning(
                        "User {UserId} not found while sending renewal reminder for {Tier}",
                        application.UserId, application.MembershipTier);
                    continue;
                }

                var email = applicant.GetEffectiveEmail() ?? applicant.Email;
                if (email is null)
                {
                    continue;
                }

                try
                {
                    var expiresFormatted = application.TermExpiresAt!.Value
                        .ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

                    await _emailService.SendTermRenewalReminderAsync(
                        email,
                        applicant.DisplayName,
                        application.MembershipTier.ToString(),
                        expiresFormatted,
                        applicant.PreferredLanguage,
                        cancellationToken);

                    await _applicationDecisionService.MarkRenewalReminderSentAsync(
                        application.Id, now, cancellationToken);
                    sentCount++;

                    _logger.LogInformation(
                        "Sent term renewal reminder to user {UserId} ({Email}) for {Tier} expiring {ExpiresAt}",
                        application.UserId, email, application.MembershipTier, application.TermExpiresAt);

                    // Dispatch in-app notification alongside email.
                    try
                    {
                        await _notificationService.SendAsync(
                            NotificationSource.TermRenewalReminder,
                            NotificationClass.Actionable,
                            NotificationPriority.Normal,
                            $"Your {application.MembershipTier} term expires {expiresFormatted}",
                            [application.UserId],
                            body: "Submit a renewal application to maintain your membership tier.",
                            actionUrl: "/Application",
                            actionLabel: "Renew →",
                            cancellationToken: cancellationToken);
                    }
                    catch (Exception notifEx)
                    {
                        _logger.LogError(notifEx,
                            "Failed to dispatch TermRenewalReminder notification for user {UserId}",
                            application.UserId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to send term renewal reminder to user {UserId} for {Tier}",
                        application.UserId, application.MembershipTier);
                }
            }

            _metrics.RecordJobRun("term_renewal_reminder", "success");
            _logger.LogInformation(
                "Completed term renewal reminder job, sent {Count} reminders",
                sentCount);
        }
        catch (Exception ex)
        {
            _metrics.RecordJobRun("term_renewal_reminder", "failure");
            _logger.LogError(ex, "Error during term renewal reminder job");
            throw;
        }
    }
}
