using Humans.Base.Extensions;
using Hangfire;
using NodaTime;
using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Services;

namespace Humans.Users.Jobs;

/// <summary>Anonymizes accounts whose deletion grace period has expired.</summary>
/// <remarks>
/// <see cref="IAccountDeletionService.AnonymizeExpiredAccountAsync"/> owns the cross-section
/// write order; the job keeps the loop, audit and confirmation email so one failure doesn't stop the run.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
internal sealed class ProcessAccountDeletionsJob(
    IUserManagement userService,
    IAccountDeletionService accountDeletionService,
    IEmailService emailService,
    UsersEmails emailMessages,
    IAuditLogService auditLogService,
    IHumansMetrics metrics,
    ILogger<ProcessAccountDeletionsJob> logger,
    IClock clock) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        logger.LogInformation(
            "Starting account deletion processing at {Time}",
            now);

        try
        {
            var dueUserIds = await userService.GetAccountsDueForAnonymizationAsync(now, cancellationToken);

            if (dueUserIds.Count == 0)
            {
                metrics.RecordJobRun("process_account_deletions", "success");
                logger.LogInformation("No accounts scheduled for deletion");
                return;
            }

            logger.LogInformation(
                "Found {Count} accounts to process for deletion",
                dueUserIds.Count);

            var processed = 0;

            foreach (var userId in dueUserIds)
            {
                try
                {
                    var summary = await accountDeletionService.AnonymizeExpiredAccountAsync(
                        userId, cancellationToken);

                    if (summary is null)
                    {
                        logger.LogWarning(
                            "Skipping account deletion for user {UserId} — no longer exists",
                            userId);
                        continue;
                    }

                    // Must not name the human: the audit log outlives erasure, so a name here
                    // would re-seed the identity just removed.
                    await auditLogService.LogAsync(
                        AuditAction.AccountAnonymized, nameof(User), userId,
                        "Account anonymized",
                        nameof(ProcessAccountDeletionsJob));

                    if (!string.IsNullOrEmpty(summary.OriginalEmail))
                    {
                        // Sent without an outbox row, so a transport failure is terminal; the
                        // erasure already succeeded and must not be reported as failed or retried.
                        try
                        {
                            await emailService.SendAsync(emailMessages.AccountDeleted(
                                summary.OriginalEmail,
                                summary.OriginalBurnerName,
                                summary.PreferredLanguage.IsSupportedCultureCode()
                                    ? summary.PreferredLanguage : CultureCatalog.DefaultCultureCode),
                                cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(
                                "Account {UserId} was anonymized but its deletion confirmation " +
                                "could not be delivered: {ExceptionType}", userId, ex.GetType().Name);
                        }
                    }

                    processed++;

                    logger.LogInformation(
                        "Successfully anonymized account {UserId}",
                        userId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to process deletion for user {UserId}",
                        userId);
                }
            }

            metrics.RecordJobRun("process_account_deletions", "success");
            logger.LogInformation(
                "Completed account deletion processing, processed {Count} accounts",
                processed);
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("process_account_deletions", "failure");
            logger.LogError(ex, "Error processing account deletions");
            throw;
        }
    }
}
