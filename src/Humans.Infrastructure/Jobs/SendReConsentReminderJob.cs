using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Users;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that sends re-consent reminders to members.
/// </summary>
/// <remarks>
/// Reads user display/email data via <see cref="IUserService"/> and persists
/// <c>User.LastConsentReminderSentAt</c> through
/// <see cref="IUserService.SetLastConsentReminderSentAsync"/>, so the job
/// never touches <see cref="Humans.Infrastructure.Data.HumansDbContext"/>
/// directly (design-rules §2c).
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class SendReConsentReminderJob(
    IMembershipCalculator membershipCalculator,
    ILegalDocumentSyncService legalDocService,
    IUserService userService,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    IOptions<EmailSettings> emailSettings,
    IHumansMetrics metrics,
    ILogger<SendReConsentReminderJob> logger,
    IClock clock) : IRecurringJob
{
    private readonly EmailSettings _emailSettings = emailSettings.Value;

    /// <summary>
    /// Sends re-consent reminders to members who haven't consented to required documents.
    /// Uses ConsentReminderDaysBeforeSuspension and ConsentReminderCooldownDays from EmailSettings.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var daysBeforeSuspension = _emailSettings.ConsentReminderDaysBeforeSuspension;
        var cooldownDays = _emailSettings.ConsentReminderCooldownDays;

        logger.LogInformation(
            "Starting re-consent reminder job at {Time}, {Days} days before suspension, {Cooldown}-day cooldown",
            clock.GetCurrentInstant(), daysBeforeSuspension, cooldownDays);

        try
        {
            var usersNeedingReminder = await membershipCalculator
                .GetUsersRequiringStatusUpdateAsync(cancellationToken);

            var requiredVersions = await legalDocService.GetRequiredVersionsAsync(cancellationToken);
            var requiredDocNames = requiredVersions
                .Select(v => v.LegalDocumentName)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var userIds = usersNeedingReminder.ToList();
            var users = await userService.GetUserInfosAsync(userIds, cancellationToken);

            var now = clock.GetCurrentInstant();
            var cooldown = Duration.FromDays(cooldownDays);
            var sentCount = 0;

            foreach (var userId in userIds)
            {
                if (!users.TryGetValue(userId, out var user))
                {
                    continue;
                }

                // Skip if a reminder was sent recently
                if (user.LastConsentReminderSentAt is not null &&
                    now - user.LastConsentReminderSentAt.Value < cooldown)
                {
                    continue;
                }

                var effectiveEmail = user.Email;
                if (effectiveEmail is not null)
                {
                    await emailService.SendAsync(emailMessages.ReConsentReminder(
                        effectiveEmail,
                        user.BurnerName,
                        requiredDocNames,
                        daysBeforeSuspension,
                        user.PreferredLanguage),
                        cancellationToken);

                    await userService.SetLastConsentReminderSentAsync(userId, now, cancellationToken);
                    sentCount++;

                    logger.LogInformation(
                        "Sent re-consent reminder to user {UserId} ({Email})",
                        userId, effectiveEmail);
                }
            }

            metrics.RecordJobRun("send_reconsent_reminder", "success");
            logger.LogInformation(
                "Completed re-consent reminder job, sent {Count} reminders ({Skipped} skipped due to cooldown)",
                sentCount, userIds.Count - sentCount);
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("send_reconsent_reminder", "failure");
            logger.LogError(ex, "Error sending re-consent reminders");
            throw;
        }
    }
}
