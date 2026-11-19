using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Humans.Base.Extensions;
using Hangfire;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Base.Interfaces;
using Humans.Base.Configuration;
using Humans.Consent.Services;
using Humans.Email.Contracts;
using Humans.Consent.Contracts;
using Humans.Users.Contracts;

namespace Humans.Consent.Jobs;

[DisableConcurrentExecution(timeoutInSeconds: 300)]
internal sealed class SendReConsentReminderJob(
    IConsentService consentService,
    ILegalDocumentSyncManagement legalDocService,
    IUserService userService,
    ConsentReminderService reminders,
    IEmailService emailService,
    ConsentEmails emailMessages,
    IOptions<EmailSettings> emailSettings,
    IHumansMetrics metrics,
    ILogger<SendReConsentReminderJob> logger,
    IClock clock) : IRecurringJob
{
    private readonly EmailSettings _emailSettings = emailSettings.Value;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var daysBeforeSuspension = _emailSettings.ConsentReminderDaysBeforeSuspension;
        var cooldownDays = _emailSettings.ConsentReminderCooldownDays;

        logger.LogInformation(
            "Starting re-consent reminder job at {Time}, {Days} days before suspension, {Cooldown}-day cooldown",
            clock.GetCurrentInstant(), daysBeforeSuspension, cooldownDays);

        var failures = new List<Exception>();
        try
        {
            var usersNeedingReminder = await consentService
                .GetUsersPastConsentDeadlineAsync(Duration.FromDays(daysBeforeSuspension), cancellationToken);

            var requiredVersions = await legalDocService.GetRequiredVersionsAsync(cancellationToken);
            var requiredDocNames = requiredVersions
                .Select(v => v.LegalDocumentName)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var userIds = usersNeedingReminder.ToList();
            var users = await userService.GetUserInfosAsync(userIds, cancellationToken);
            var lastSent = await reminders.GetLastSentAsync(users.Values, cancellationToken);

            var now = clock.GetCurrentInstant();
            var cooldown = Duration.FromDays(cooldownDays);
            var sentCount = 0;

            foreach (var userId in userIds)
            {
                // Keep earlier delivery failures visible if shutdown stops later recipients.
                if (failures.Count > 0 && cancellationToken.IsCancellationRequested)
                    break;
                cancellationToken.ThrowIfCancellationRequested();

                if (!users.TryGetValue(userId, out var user))
                {
                    continue;
                }

                var previouslySent = lastSent.TryGetValue(user.Id, out var sentAt);
                if (previouslySent && now - sentAt < cooldown)
                {
                    continue;
                }

                var effectiveEmail = user.Email;
                if (effectiveEmail is not null)
                {
                    try
                    {
                        var language = user.PreferredLanguage;
                        var message = emailMessages.ReConsentReminder(
                            user.Id,
                            effectiveEmail,
                            user.BurnerName,
                            requiredDocNames,
                            daysBeforeSuspension,
                            language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode);
                        // Only a successful stamp advances the delivery cycle; retrying keeps its prior anchor.
                        var previousTicks = previouslySent ? sentAt.ToUnixTimeTicks() : (long?)null;
                        var deliveryKey = string.Create(CultureInfo.InvariantCulture,
                            $"{message.TemplateName}:{user.Id:N}:{previousTicks}");
                        var deliveryId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(deliveryKey)).AsSpan(0, 16));
                        // Finish the queued reminder's cooldown stamp before observing shutdown again.
                        await emailService.SendAsync(message with { DeliveryId = deliveryId }, CancellationToken.None);

                        await reminders.RecordSentAsync(user.Id, now, CancellationToken.None);
                        sentCount++;

                        logger.LogInformation(
                            "Sent re-consent reminder to user {UserId} ({Email})",
                            user.Id, effectiveEmail);
                    }
                    catch (OperationCanceledException ex)
                    {
                        // Enqueue and stamp are detached: their cancellation is a dependency fault.
                        failures.Add(ex);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex);
                        logger.LogError(ex, "Failed to complete re-consent reminder for user {UserId}", user.Id);
                    }
                }
            }

            if (failures.Count > 0)
                throw new AggregateException("Failed to complete one or more re-consent reminders.", failures);

            metrics.RecordJobRun("send_reconsent_reminder", "success");
            logger.LogInformation(
                "Completed re-consent reminder job, sent {Count} reminders ({Skipped} skipped)",
                sentCount, userIds.Count - sentCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && failures.Count == 0)
        {
            logger.LogWarning("Re-consent reminder job cancelled during shutdown");
            throw;
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("send_reconsent_reminder", "failure");
            logger.LogError(ex, "Error sending re-consent reminders");
            throw;
        }
    }
}
