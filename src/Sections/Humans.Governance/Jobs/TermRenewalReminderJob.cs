using System.Globalization;
using System.Resources;
using Hangfire;
using NodaTime;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Governance.Services;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;

namespace Humans.Governance.Jobs;

/// <summary>Reminds members whose term expires within 90 days to apply for renewal.</summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
internal sealed class TermRenewalReminderJob(
    IGovernanceManagement applicationDecisionService,
    IUserService userService,
    IEmailService emailService,
    GovernanceEmails emailMessages,
    INotificationEmitter notificationService,
    IHumansMetrics metrics,
    ILogger<TermRenewalReminderJob> logger,
    IClock clock) : IRecurringJob
{
    private static readonly ResourceManager NoticeResources = new(typeof(GovernanceResource));

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting term renewal reminder job at {Time}", clock.GetCurrentInstant());

        try
        {
            var today = clock.GetCurrentInstant().InUtc().Date;
            var reminderThreshold = TermExpiryCalculator.GetRenewalThreshold(today);

            var expiringApplications = await applicationDecisionService
                .GetExpiringApplicationsNeedingReminderAsync(today, reminderThreshold, cancellationToken);

            var pendingSet = await applicationDecisionService
                .GetPendingApplicationUserTiersAsync(cancellationToken);

            var applicantIds = expiringApplications
                .Select(a => a.UserId)
                .Distinct()
                .ToList();
            var applicantsById = applicantIds.Count == 0
                ? new Dictionary<Guid, UserInfo>()
                : (await userService.GetUserInfosAsync(applicantIds, cancellationToken))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

            var sentCount = 0;
            var failures = new List<Exception>();
            var now = clock.GetCurrentInstant();

            foreach (var application in expiringApplications)
            {
                // Preserve earlier delivery failures when shutdown stops the remaining recipients.
                if (failures.Count > 0 && cancellationToken.IsCancellationRequested)
                    break;
                cancellationToken.ThrowIfCancellationRequested();

                if (pendingSet.Contains((application.UserId, application.MembershipTier)))
                {
                    logger.LogDebug(
                        "Skipping renewal reminder for user {UserId} tier {Tier} — pending application exists",
                        application.UserId, application.MembershipTier);
                    continue;
                }

                if (!applicantsById.TryGetValue(application.UserId, out var applicant))
                {
                    logger.LogWarning(
                        "User {UserId} not found while sending renewal reminder for {Tier}",
                        application.UserId, application.MembershipTier);
                    continue;
                }

                var email = applicant.Email;
                if (string.IsNullOrWhiteSpace(email))
                {
                    logger.LogWarning(
                        "Skipping term renewal reminder for user {UserId} tier {Tier}: no notification email",
                        application.UserId, application.MembershipTier);
                    continue;
                }

                try
                {
                    var language = applicant.PreferredLanguage.IsSupportedCultureCode()
                        ? applicant.PreferredLanguage : CultureCatalog.DefaultCultureCode;

                    // Finish this recipient's enqueue, sent stamp and notice before observing shutdown again.
                    await emailService.SendAsync(emailMessages.TermRenewalReminder(
                        applicant.Id,
                        email,
                        applicant.BurnerName,
                        application.MembershipTier.ToString(),
                        application.TermExpiresAt!.Value,
                        language) with
                    { DeliveryId = application.Id },
                        CancellationToken.None);

                    await applicationDecisionService.MarkRenewalReminderSentAsync(
                        application.Id, now, CancellationToken.None);
                    sentCount++;

                    logger.LogInformation(
                        "Sent term renewal reminder to user {UserId} ({Email}) for {Tier} expiring {ExpiresAt}",
                        application.UserId, email, application.MembershipTier, application.TermExpiresAt);

                    try
                    {
                        var culture = CultureInfo.GetCultureInfo(language);
                        using var cultureScope = new CultureScope(culture.Name);
                        var expiryDate = application.TermExpiresAt.Value.ToDate();
                        await notificationService.SendAsync(
                            GovernanceNotificationSources.TermRenewalReminder,
                            NotificationClass.Actionable,
                            NotificationPriority.Normal,
                            string.Format(culture, NoticeResources.GetString("Governance_Notification_TermRenewalReminder", culture)!, application.MembershipTier, expiryDate),
                            [applicant.Id],
                            body: NoticeResources.GetString("Governance_Notification_TermRenewalReminderBody", culture),
                            actionUrl: "/Governance/Applications",
                            actionLabel: NoticeResources.GetString("Governance_Notification_Renew", culture),
                            cancellationToken: CancellationToken.None);
                    }
                    catch (Exception notifEx)
                    {
                        logger.LogError(notifEx,
                            "Failed to dispatch TermRenewalReminder notification for user {UserId}",
                            application.UserId);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    logger.LogError(ex,
                        "Failed to send term renewal reminder to user {UserId} for {Tier}",
                        application.UserId, application.MembershipTier);
                }
            }

            if (failures.Count > 0)
                throw new AggregateException("Failed to complete one or more term renewal reminders.", failures);

            metrics.RecordJobRun("term_renewal_reminder", "success");
            logger.LogInformation(
                "Completed term renewal reminder job, sent {Count} reminders",
                sentCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Term renewal reminder job cancelled during shutdown");
            throw;
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("term_renewal_reminder", "failure");
            logger.LogError(ex, "Error during term renewal reminder job");
            throw;
        }
    }
}
