using System.Diagnostics.Metrics;
using Humans.Application.Interfaces.Metering;
using Humans.Application.Metering;
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
/// <see cref="IUserService.AnonymizeExpiredAccountAsync"/> â€” the Users section
/// owns the User aggregate and coordinates cross-section writes (profile,
/// team memberships, role assignments, shift signups, volunteer event
/// profiles) through their owning services. The job retains the loop +
/// audit-log + confirmation-email orchestration so a per-user failure
/// doesn't stop the run.
/// </remarks>
public class ProcessAccountDeletionsJob : IRecurringJob
{
    private readonly IUserService _userService;
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLogService;
    private readonly Counter<long> _jobRunsCounter;
    private readonly ILogger<ProcessAccountDeletionsJob> _logger;
    private readonly IClock _clock;

    public ProcessAccountDeletionsJob(
        IUserService userService,
        IEmailService emailService,
        IAuditLogService auditLogService,
        IMeters meters,
        ILogger<ProcessAccountDeletionsJob> logger,
        IClock clock)
    {
        _userService = userService;
        _emailService = emailService;
        _auditLogService = auditLogService;
        _jobRunsCounter = meters.RegisterCounter(
            "humans.job_runs_total",
            new MeterMetadata("Total background job runs", "{runs}"));
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
                _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "process_account_deletions"),
                new KeyValuePair<string, object?>("result", "success"));
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
                    var summary = await _userService.AnonymizeExpiredAccountAsync(
                        userId, now, cancellationToken);

                    if (summary is null)
                    {
                        // User disappeared between enumeration and anonymization.
                        _logger.LogWarning(
                            "Skipping account deletion for user {UserId} â€” no longer exists",
                            userId);
                        continue;
                    }

                    // Audit AFTER the business save has succeeded, per design-rules Â§7a.
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

            _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "process_account_deletions"),
                new KeyValuePair<string, object?>("result", "success"));
            _logger.LogInformation(
                "Completed account deletion processing, processed {Count} accounts",
                processed);
        }
        catch (Exception ex)
        {
            _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "process_account_deletions"),
                new KeyValuePair<string, object?>("result", "failure"));
            _logger.LogError(ex, "Error processing account deletions");
            throw;
        }
    }
}
