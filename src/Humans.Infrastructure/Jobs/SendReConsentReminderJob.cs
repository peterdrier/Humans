using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that sends re-consent reminders to members.
/// </summary>
public class SendReConsentReminderJob : IRecurringJob
{
    private readonly HumansDbContext _dbContext;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly ILegalDocumentSyncService _legalDocService;
    private readonly IEmailService _emailService;
    private readonly IUserEmailService _userEmailService;
    private readonly EmailSettings _emailSettings;
    private readonly IHumansMetrics _metrics;
    private readonly ILogger<SendReConsentReminderJob> _logger;
    private readonly IClock _clock;

    public SendReConsentReminderJob(
        HumansDbContext dbContext,
        IMembershipCalculator membershipCalculator,
        ILegalDocumentSyncService legalDocService,
        IEmailService emailService,
        IUserEmailService userEmailService,
        IOptions<EmailSettings> emailSettings,
        IHumansMetrics metrics,
        ILogger<SendReConsentReminderJob> logger,
        IClock clock)
    {
        _dbContext = dbContext;
        _membershipCalculator = membershipCalculator;
        _legalDocService = legalDocService;
        _emailService = emailService;
        _userEmailService = userEmailService;
        _emailSettings = emailSettings.Value;
        _metrics = metrics;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Sends re-consent reminders to members who haven't consented to required documents.
    /// Uses ConsentReminderDaysBeforeSuspension and ConsentReminderCooldownDays from EmailSettings.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var daysBeforeSuspension = _emailSettings.ConsentReminderDaysBeforeSuspension;
        var cooldownDays = _emailSettings.ConsentReminderCooldownDays;

        _logger.LogInformation(
            "Starting re-consent reminder job at {Time}, {Days} days before suspension, {Cooldown}-day cooldown",
            _clock.GetCurrentInstant(), daysBeforeSuspension, cooldownDays);

        try
        {
            var usersNeedingReminder = await _membershipCalculator
                .GetUsersRequiringStatusUpdateAsync(cancellationToken);

            var requiredVersions = await _legalDocService.GetRequiredVersionsAsync(cancellationToken);
            var requiredDocNames = requiredVersions
                .Select(v => v.LegalDocument.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var userIds = usersNeedingReminder.ToList();
            var users = await _dbContext.Users
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, cancellationToken);

            var effectiveEmails = await _userEmailService
                .GetNotificationEmailsByUserIdsAsync(userIds, cancellationToken);

            var now = _clock.GetCurrentInstant();
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

                var effectiveEmail = effectiveEmails.TryGetValue(userId, out var e) ? e : null;
                if (effectiveEmail is not null)
                {
                    await _emailService.SendReConsentReminderAsync(
                        effectiveEmail,
                        user.DisplayName,
                        requiredDocNames,
                        daysBeforeSuspension,
                        user.PreferredLanguage,
                        cancellationToken);

                    user.LastConsentReminderSentAt = now;
                    sentCount++;

                    _logger.LogInformation(
                        "Sent re-consent reminder to user {UserId} ({Email})",
                        userId, effectiveEmail);
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            _metrics.RecordJobRun("send_reconsent_reminder", "success");
            _logger.LogInformation(
                "Completed re-consent reminder job, sent {Count} reminders ({Skipped} skipped due to cooldown)",
                sentCount, userIds.Count - sentCount);
        }
        catch (Exception ex)
        {
            _metrics.RecordJobRun("send_reconsent_reminder", "failure");
            _logger.LogError(ex, "Error sending re-consent reminders");
            throw;
        }
    }
}
