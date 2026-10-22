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

internal sealed class ExpenseReportBackgroundProcessor(
    IExpenseRepository repo,
    IFileStorage fileStorage,
    IUserService userService,
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
    /// With the 2^n-minute backoff, ten attempts span roughly 17 hours: long enough to ride out a Holded outage, short
    /// enough that a broken push surfaces on /Expenses/Review the same day.
    /// </summary>
    internal const int MaxOutboxRetries = 10;

    /// <summary>Audit actor for these unattended pushes; matches the Hangfire job's type name.</summary>
    internal const string OutboxJobName = "HoldedExpenseOutboxJob";

    /// <summary>
    /// One drain at a time; one process on one server makes this enough. The job's
    /// <c>[DisableConcurrentExecution]</c> stays as the outer guard. Static because the processor is Scoped.
    /// </summary>
    private static readonly SemaphoreSlim DrainGate = new(1, 1);

    public async Task DrainHoldedOutboxAsync(int batchSize, CancellationToken ct = default)
    {
        if (!await DrainGate.WaitAsync(0, ct))
        {
            logger.LogInformation(
                "Holded outbox drain already running in this process; skipping this tick");
            return;
        }

        try
        {
            // No API key (PR previews, local dev): don't drain, or every 401 would write off a queued event. The
            // same flag makes /Expenses/{id} show NotConfigured instead of Queued.
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
                            // v2 has no tag or doc-update endpoint (PUT /purchases/{id} replaces the doc and has no
                            // tags), so recategorizing happens inside Holded. The enum member stays so old queued rows
                            // drain instead of poisoning the outbox.
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
        finally
        {
            DrainGate.Release();
        }
    }

    /// <summary>
    /// The outbox columns are unreadable outside the database and do not survive cleanup; the audit entry keeps the
    /// failure on the report's history.
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
