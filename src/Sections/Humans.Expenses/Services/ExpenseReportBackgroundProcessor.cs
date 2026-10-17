using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Expenses.Domain;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Expenses.Services;

/// <summary>Drains the expense outbox, preserving retries and permanent-failure audits.</summary>
internal sealed class ExpenseReportBackgroundProcessor(
    IExpenseRepository repo,
    IFileStorage fileStorage,
    IUserServiceRead userService,
    IHoldedClient holdedClient,
    IHoldedFinanceService holdedFinance,
    IHoldedFinanceWrite holdedFinanceWrite,
    IAuditLogService auditLogService,
    IClock clock,
    ILogger<ExpenseReportBackgroundProcessor> logger) : IExpenseReportBackgroundProcessor, IApplicationService
{
    private readonly ExpenseHoldedPublisher holdedPublisher = new(
        repo, fileStorage, userService, holdedClient, holdedFinance, holdedFinanceWrite, auditLogService, logger);

    /// <summary>
    /// Attempts a Holded push gets before it is written off. With the 2^n-minute backoff below,
    /// ten attempts span roughly 17 hours — long enough to ride out a Holded outage, short enough
    /// that a genuinely broken push surfaces on /Expenses/Review the same day.
    /// </summary>
    internal const int MaxOutboxRetries = 10;

    /// <summary>Audit actor for pushes, which run unattended. Matches the Hangfire job's type name.</summary>
    internal const string OutboxJobName = "HoldedExpenseOutboxJob";

    public async Task DrainHoldedOutboxAsync(int batchSize, CancellationToken ct = default)
    {
        // No Holded API key (PR-preview / local dev envs) → don't drain. A 401 here is a permanent
        // error that would write off every queued event. Debug-level: this job runs every minute.
        // The same flag is what makes /Expenses/{id} report NotConfigured instead of Queued.
        if (!holdedClient.IsConfigured)
        {
            logger.LogDebug(
                "HOLDED_API_KEY_V2 not configured — skipping Holded expense outbox drain.");
            return;
        }

        var events = await repo
            .GetUnprocessedOutboxAsync(clock.GetCurrentInstant(), batchSize, ct);

        if (events.Count == 0)
        {
            return;
        }

        foreach (var outboxEvent in events)
        {
            try
            {
                var report = await repo
                    .GetByIdAsync(outboxEvent.ExpenseReportId, ct);

                if (report is null)
                {
                    logger.LogWarning(
                        "Outbox event {OutboxEventId} references missing report {ReportId} — marking permanently failed",
                        outboxEvent.Id, outboxEvent.ExpenseReportId);
                    await WriteOffOutboxEventAsync(
                        outboxEvent, "Report not found", ct);
                    continue;
                }

                var now = clock.GetCurrentInstant();

                switch (outboxEvent.EventType)
                {
                    case HoldedExpenseOutboxEventType.CreateIncomingDoc:
                        await holdedPublisher.PublishAsync(
                            outboxEvent.Id, report, now, ct);
                        break;

                    case HoldedExpenseOutboxEventType.UpdateIncomingDocTag:
                        // v2 has no tag/doc-update endpoint (PUT /purchases/{id} is a full-replacement
                        // update with no tags field, and no separate tag-assignment endpoint exists).
                        // Recategorize-after-push is now done by reclassifying the line inside Holded
                        // directly; the ledger mirror + reconciliation pull the correction back. The
                        // enum member stays so any queued rows from before this change still drain
                        // instead of poisoning the outbox.
                        logger.LogInformation(
                            "Skipping UpdateIncomingDocTag outbox event {OutboxEventId} for report " +
                            "{ReportId} — Holded v2 has no tag/doc-update endpoint; recategorize is " +
                            "now done by reclassifying the line directly in Holded.",
                            outboxEvent.Id, report.Id);
                        await repo.MarkOutboxProcessedAsync(outboxEvent.Id, now, ct);
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Unknown outbox event type '{outboxEvent.EventType}'.");
                }
            }
            catch (HoldedTransientException ex)
            {
                var attempts = outboxEvent.RetryCount + 1;
                if (attempts >= MaxOutboxRetries)
                {
                    logger.LogError(
                        ex,
                        "Holded outbox event {OutboxEventId} exhausted its {MaxRetries} attempts — writing it off",
                        outboxEvent.Id, MaxOutboxRetries);
                    await WriteOffOutboxEventAsync(
                        outboxEvent,
                        $"Gave up after {attempts} attempts. Last error: {ex.Message}",
                        ct);
                }
                else
                {
                    // Same curve as the Email outbox: 2, 4, 8 … minutes, so a Holded outage longer
                    // than a few minutes is survived instead of being re-hit every 60 seconds.
                    var nextRetryAt = clock.GetCurrentInstant()
                        + Duration.FromMinutes((long)Math.Pow(2, attempts));
                    logger.LogWarning(
                        ex,
                        "Transient error processing Holded outbox event {OutboxEventId} — attempt {Attempt}/{MaxRetries}, retrying at {NextRetryAt}",
                        outboxEvent.Id, attempts, MaxOutboxRetries, nextRetryAt);
                    await repo.IncrementOutboxRetryAsync(
                        outboxEvent.Id, BoundOutboxError(ex.Message), nextRetryAt, ct);
                }
            }
            catch (HoldedPermanentException ex)
            {
                logger.LogError(
                    ex,
                    "Permanent error processing Holded outbox event {OutboxEventId} — HTTP {StatusCode}",
                    outboxEvent.Id, ex.StatusCode);
                await WriteOffOutboxEventAsync(outboxEvent, ex.Message, ct);
            }
        }
    }

    /// <summary>
    /// Writes an outbox event off and records why in the audit log. The outbox columns alone are
    /// not readable outside the database and do not survive row cleanup; the audit entry is what
    /// keeps "this push failed, here is the error" on the report's history
    /// (nobodies-collective/Humans#1045).
    /// </summary>
    private async Task WriteOffOutboxEventAsync(
        HoldedExpenseOutboxEvent outboxEvent, string error, CancellationToken ct)
    {
        error = BoundOutboxError(error);
        await repo.MarkOutboxFailedPermanentlyAsync(
            outboxEvent.Id, error, clock.GetCurrentInstant(), ct);

        await auditLogService.LogAsync(
            AuditAction.ExpenseHoldedFailed,
            AuditEntityTypes.Report, outboxEvent.ExpenseReportId,
            $"Holded push failed permanently: {error}",
            OutboxJobName);
    }

    // Fits the outbox's varchar(2000) and the prefixed audit description; logs retain the exception.
    private static string BoundOutboxError(string error)
    {
        const int maxLength = 2000;
        if (error.Length <= maxLength) return error;
        var length = maxLength;
        if (char.IsHighSurrogate(error[length - 1]) && char.IsLowSurrogate(error[length]))
            length--;
        return error[..length];
    }

}
