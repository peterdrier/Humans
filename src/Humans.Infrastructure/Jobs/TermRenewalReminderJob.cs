using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;

namespace Humans.Infrastructure.Jobs;

public class TermRenewalReminderJob
{
    private readonly HumansDbContext _dbContext;
    private readonly IEmailService _emailService;
    private readonly HumansMetricsService _metrics;
    private readonly ILogger<TermRenewalReminderJob> _logger;
    private readonly IClock _clock;

    private const int ReminderDaysBeforeExpiry = 90;

    public TermRenewalReminderJob(
        HumansDbContext dbContext,
        IEmailService emailService,
        HumansMetricsService metrics,
        ILogger<TermRenewalReminderJob> logger,
        IClock clock)
    {
        _dbContext = dbContext;
        _emailService = emailService;
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

            // Find approved applications with term expiring within 90 days that haven't had a reminder sent
            var expiringApplications = await _dbContext.Applications
                .Include(a => a.User)
                    .ThenInclude(u => u.UserEmails)
                .Where(a =>
                    a.Status == ApplicationStatus.Approved &&
                    a.TermExpiresAt != null &&
                    a.TermExpiresAt <= reminderThreshold &&
                    a.TermExpiresAt >= today &&
                    a.RenewalReminderSentAt == null)
                .ToListAsync(cancellationToken);

            // For each user, only consider the latest approved application per tier
            // (a user might have multiple approved applications if they renewed before)
            var latestPerUserTier = expiringApplications
                .GroupBy(a => new { a.UserId, a.MembershipTier })
                .Select(g => g.OrderByDescending(a => a.SubmittedAt).First())
                .ToList();

            // Exclude users who already have a pending renewal application for the same tier
            var userTiersWithPending = await _dbContext.Applications
                .Where(a =>
                    a.Status == ApplicationStatus.Submitted)
                .Select(a => new { a.UserId, a.MembershipTier })
                .ToListAsync(cancellationToken);

            var pendingSet = userTiersWithPending
                .Select(x => (x.UserId, x.MembershipTier))
                .ToHashSet();

            var sentCount = 0;

            foreach (var application in latestPerUserTier)
            {
                if (pendingSet.Contains((application.UserId, application.MembershipTier)))
                {
                    _logger.LogDebug(
                        "Skipping renewal reminder for user {UserId} tier {Tier} — pending application exists",
                        application.UserId, application.MembershipTier);
                    continue;
                }

                var email = application.User.GetEffectiveEmail() ?? application.User.Email;
                if (email == null)
                {
                    continue;
                }

                try
                {
                    var expiresFormatted = application.TermExpiresAt!.Value
                        .ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

                    await _emailService.SendTermRenewalReminderAsync(
                        email,
                        application.User.DisplayName,
                        application.MembershipTier.ToString(),
                        expiresFormatted,
                        application.User.PreferredLanguage,
                        cancellationToken);

                    application.RenewalReminderSentAt = _clock.GetCurrentInstant();
                    sentCount++;

                    _logger.LogInformation(
                        "Sent term renewal reminder to user {UserId} ({Email}) for {Tier} expiring {ExpiresAt}",
                        application.UserId, email, application.MembershipTier, application.TermExpiresAt);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to send term renewal reminder to user {UserId} for {Tier}",
                        application.UserId, application.MembershipTier);
                }
            }

            if (sentCount > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
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
