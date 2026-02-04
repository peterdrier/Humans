using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Profiles.Application.Interfaces;
using Profiles.Infrastructure.Data;

namespace Profiles.Infrastructure.Jobs;

/// <summary>
/// Background job that sends re-consent reminders to members.
/// </summary>
public class SendReConsentReminderJob
{
    private readonly ProfilesDbContext _dbContext;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly ILegalDocumentSyncService _legalDocService;
    private readonly IEmailService _emailService;
    private readonly ILogger<SendReConsentReminderJob> _logger;
    private readonly IClock _clock;

    public SendReConsentReminderJob(
        ProfilesDbContext dbContext,
        IMembershipCalculator membershipCalculator,
        ILegalDocumentSyncService legalDocService,
        IEmailService emailService,
        ILogger<SendReConsentReminderJob> logger,
        IClock clock)
    {
        _dbContext = dbContext;
        _membershipCalculator = membershipCalculator;
        _legalDocService = legalDocService;
        _emailService = emailService;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Sends re-consent reminders to members who haven't consented to required documents.
    /// </summary>
    /// <param name="daysBeforeSuspension">Number of days before suspension to send reminder.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ExecuteAsync(int daysBeforeSuspension = 7, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Starting re-consent reminder job at {Time}, {Days} days before suspension",
            _clock.GetCurrentInstant(), daysBeforeSuspension);

        try
        {
            var usersNeedingReminder = await _membershipCalculator
                .GetUsersRequiringStatusUpdateAsync(cancellationToken);

            var requiredVersions = await _legalDocService.GetRequiredVersionsAsync(cancellationToken);
            var requiredDocNames = requiredVersions
                .Select(v => v.LegalDocument.Name)
                .Distinct()
                .ToList();

            foreach (var userId in usersNeedingReminder)
            {
                var user = await _dbContext.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

                if (user?.Email != null)
                {
                    await _emailService.SendReConsentReminderAsync(
                        user.Email,
                        user.DisplayName,
                        requiredDocNames,
                        daysBeforeSuspension,
                        cancellationToken);

                    _logger.LogInformation(
                        "Sent re-consent reminder to user {UserId} ({Email})",
                        userId, user.Email);
                }
            }

            _logger.LogInformation(
                "Completed re-consent reminder job, sent {Count} reminders",
                usersNeedingReminder.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending re-consent reminders");
            throw;
        }
    }
}
