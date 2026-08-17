using Hangfire;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Domain.Enums;
using Humans.Users.Contracts;

namespace Humans.Users.Jobs;

/// <summary>
/// Background job that processes scheduled account deletions.
/// Runs daily to anonymize accounts where the 30-day grace period has expired.
/// </summary>
/// <remarks>
/// Delegates the actual anonymization to
/// <see cref="IAccountDeletionService.AnonymizeExpiredAccountAsync"/> — the
/// orchestrator owns the cross-section write order (team memberships, role
/// assignments, profile anonymization, shift signup cancellation, volunteer-
/// event-profile deletion, User-aggregate identity collapse) and every
/// deletion-related cache invalidation. The job retains the loop +
/// audit-log + confirmation-email orchestration so a per-user failure
/// doesn't stop the run. Candidate enumeration stays on
/// <see cref="IUserService"/> because <c>DeletionScheduledFor</c> is a User
/// column (owning-section rule).
///
/// Moved out of <c>Humans.Infrastructure/Jobs</c> at G5 lane 5b-4
/// (nobodies-collective/Humans#866). It sits under <c>Jobs/</c> because Shell
/// names the concrete type at registration and HUM0034 makes every other public
/// type in a section assembly an error.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class ProcessAccountDeletionsJob(
    IUserServiceRead userService,
    IAccountDeletionService accountDeletionService,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    IAuditLogService auditLogService,
    IHumansMetrics metrics,
    ILogger<ProcessAccountDeletionsJob> logger,
    IClock clock) : IRecurringJob
{
    /// <summary>
    /// Processes all accounts scheduled for deletion where the grace period has expired.
    /// </summary>
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
                        // User disappeared between enumeration and anonymization.
                        logger.LogWarning(
                            "Skipping account deletion for user {UserId} — no longer exists",
                            userId);
                        continue;
                    }

                    // Audit AFTER the business save has succeeded, per design-rules §7a.
                    await auditLogService.LogAsync(
                        AuditAction.AccountAnonymized, nameof(User), userId,
                        $"Account anonymized (was {summary.OriginalDisplayName})",
                        nameof(ProcessAccountDeletionsJob));

                    foreach (var (signupId, shiftId) in summary.CancelledSignupIds)
                    {
                        await auditLogService.LogAsync(
                            // Literal, not nameof: ShiftSignup is internal to Humans.Shifts
                            // since its G5 move, and the discriminator is a persisted string
                            // (memory/code/type-name-as-persisted-string.md).
                            AuditAction.ShiftSignupCancelled, "ShiftSignup", signupId,
                            $"Cancelled signup (account deletion) for shift {shiftId}",
                            nameof(ProcessAccountDeletionsJob));
                    }

                    if (!string.IsNullOrEmpty(summary.OriginalEmail))
                    {
                        await emailService.SendAsync(emailMessages.AccountDeleted(
                            summary.OriginalEmail,
                            summary.OriginalDisplayName,
                            summary.PreferredLanguage),
                            cancellationToken);
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
