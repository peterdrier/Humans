using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Jobs;

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
/// </remarks>
public class ProcessAccountDeletionsJob : IRecurringJob
{
    private readonly IUserService _userService;
    private readonly IAccountDeletionService _accountDeletionService;
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLogService;
    private readonly IHumansMetrics _metrics;
    private readonly ILogger<ProcessAccountDeletionsJob> _logger;
    private readonly IClock _clock;

    public ProcessAccountDeletionsJob(
        IUserService userService,
        IAccountDeletionService accountDeletionService,
        IEmailService emailService,
        IAuditLogService auditLogService,
        IHumansMetrics metrics,
        ILogger<ProcessAccountDeletionsJob> logger,
        IClock clock)
    {
        _userService = userService;
        _accountDeletionService = accountDeletionService;
        _emailService = emailService;
        _auditLogService = auditLogService;
        _metrics = metrics;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Processes all accounts scheduled for deletion where the grace period has expired.
    /// </summary>
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        _logger.LogInformation(
            "Starting account deletion processing at {Time}",
            now);

        try
        {
            var dueUserIds = await _userService.GetAccountsDueForAnonymizationAsync(now, cancellationToken);

            if (dueUserIds.Count == 0)
            {
                _metrics.RecordJobRun("process_account_deletions", "success");
                _logger.LogInformation("No accounts scheduled for deletion");
                return;
            }

            _logger.LogInformation(
                "Found {Count} accounts to process for deletion",
                dueUserIds.Count);

            var processed = 0;

            foreach (var userId in dueUserIds)
            {
                try
                {
                    var summary = await _accountDeletionService.AnonymizeExpiredAccountAsync(
                        userId, now, cancellationToken);

                    if (summary is null)
                    {
                        // User disappeared between enumeration and anonymization.
                        _logger.LogWarning(
                            "Skipping account deletion for user {UserId} — no longer exists",
                            userId);
                        continue;
                    }

                    // Audit AFTER the business save has succeeded, per design-rules §7a.
                    await _auditLogService.LogAsync(
                        AuditAction.AccountAnonymized, nameof(User), userId,
                        $"Account anonymized (was {summary.OriginalDisplayName})",
                        nameof(ProcessAccountDeletionsJob));

                    foreach (var (signupId, shiftId) in summary.CancelledSignupIds)
                    {
                        await _auditLogService.LogAsync(
                            AuditAction.ShiftSignupCancelled, nameof(ShiftSignup), signupId,
                            $"Cancelled signup (account deletion) for shift {shiftId}",
                            nameof(ProcessAccountDeletionsJob));
                    }

                    if (!string.IsNullOrEmpty(summary.OriginalEmail))
                    {
                        await _emailService.SendAccountDeletedAsync(
                            summary.OriginalEmail,
                            summary.OriginalDisplayName,
                            summary.PreferredLanguage,
                            cancellationToken);
                    }

                    processed++;

                    _logger.LogInformation(
                        "Successfully anonymized account {UserId}",
                        userId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to process deletion for user {UserId}",
                        userId);
                }
            }

            _metrics.RecordJobRun("process_account_deletions", "success");
            _logger.LogInformation(
                "Completed account deletion processing, processed {Count} accounts",
                processed);
        }
        catch (Exception ex)
        {
            _metrics.RecordJobRun("process_account_deletions", "failure");
            _logger.LogError(ex, "Error processing account deletions");
            throw;
        }
    }
}
