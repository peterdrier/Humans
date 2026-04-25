using System.Diagnostics.Metrics;
using Humans.Application.Interfaces.Metering;
using Humans.Application.Metering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Application.Extensions;
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
/// directly (design-rules Â§2c).
/// </remarks>
public class SendReConsentReminderJob : IRecurringJob
{
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly ILegalDocumentSyncService _legalDocService;
    private readonly IUserService _userService;
    private readonly IEmailService _emailService;
    private readonly EmailSettings _emailSettings;
    private readonly Counter<long> _jobRunsCounter;
    private readonly ILogger<SendReConsentReminderJob> _logger;
    private readonly IClock _clock;

    public SendReConsentReminderJob(
        IMembershipCalculator membershipCalculator,
        ILegalDocumentSyncService legalDocService,
        IUserService userService,
        IEmailService emailService,
        IOptions<EmailSettings> emailSettings,
        IMeters meters,
        ILogger<SendReConsentReminderJob> logger,
        IClock clock)
    {
        _membershipCalculator = membershipCalculator;
        _legalDocService = legalDocService;
        _userService = userService;
        _emailService = emailService;
        _emailSettings = emailSettings.Value;
        _jobRunsCounter = meters.RegisterCounter(
            "humans.job_runs_total",
            new MeterMetadata("Total background job runs", "{runs}"));
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
            var users = await _userService.GetByIdsWithEmailsAsync(userIds, cancellationToken);

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

                var effectiveEmail = user.GetEffectiveEmail();
                if (effectiveEmail is not null)
                {
                    await _emailService.SendReConsentReminderAsync(
                        effectiveEmail,
                        user.DisplayName,
                        requiredDocNames,
                        daysBeforeSuspension,
                        user.PreferredLanguage,
                        cancellationToken);

                    await _userService.SetLastConsentReminderSentAsync(userId, now, cancellationToken);
                    sentCount++;

                    _logger.LogInformation(
                        "Sent re-consent reminder to user {UserId} ({Email})",
                        userId, effectiveEmail);
                }
            }

            _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "send_reconsent_reminder"),
                new KeyValuePair<string, object?>("result", "success"));
            _logger.LogInformation(
                "Completed re-consent reminder job, sent {Count} reminders ({Skipped} skipped due to cooldown)",
                sentCount, userIds.Count - sentCount);
        }
        catch (Exception ex)
        {
            _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "send_reconsent_reminder"),
                new KeyValuePair<string, object?>("result", "failure"));
            _logger.LogError(ex, "Error sending re-consent reminders");
            throw;
        }
    }
}
