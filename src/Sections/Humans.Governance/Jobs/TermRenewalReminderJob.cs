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
    private const int ReminderDaysBeforeExpiry = 90;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting term renewal reminder job at {Time}", clock.GetCurrentInstant());

        try
        {
            var today = clock.GetCurrentInstant().InUtc().Date;
            var reminderThreshold = today.PlusDays(ReminderDaysBeforeExpiry);

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
            var now = clock.GetCurrentInstant();

            foreach (var application in expiringApplications)
            {
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
                if (email is null)
                {
                    continue;
                }

                try
                {
                    var language = applicant.PreferredLanguage.IsSupportedCultureCode()
                        ? applicant.PreferredLanguage : CultureCatalog.DefaultCultureCode;

                    await emailService.SendAsync(emailMessages.TermRenewalReminder(
                        applicant.Id,
                        email,
                        applicant.BurnerName,
                        application.MembershipTier.ToString(),
                        application.TermExpiresAt!.Value,
                        language),
                        cancellationToken);

                    await applicationDecisionService.MarkRenewalReminderSentAsync(
                        application.Id, now, cancellationToken);
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
                            NotificationSource.TermRenewalReminder,
                            NotificationClass.Actionable,
                            NotificationPriority.Normal,
                            string.Format(culture, NoticeResources.GetString("Governance_Notification_TermRenewalReminder", culture)!, application.MembershipTier, expiryDate),
                            [application.UserId],
                            body: NoticeResources.GetString("Governance_Notification_TermRenewalReminderBody", culture),
                            actionUrl: "/Governance/Applications",
                            actionLabel: NoticeResources.GetString("Governance_Notification_Renew", culture),
                            cancellationToken: cancellationToken);
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
                    logger.LogError(ex,
                        "Failed to send term renewal reminder to user {UserId} for {Tier}",
                        application.UserId, application.MembershipTier);
                }
            }

            metrics.RecordJobRun("term_renewal_reminder", "success");
            logger.LogInformation(
                "Completed term renewal reminder job, sent {Count} reminders",
                sentCount);
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("term_renewal_reminder", "failure");
            logger.LogError(ex, "Error during term renewal reminder job");
            throw;
        }
    }
}
