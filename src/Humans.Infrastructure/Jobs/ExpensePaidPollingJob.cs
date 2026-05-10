using Hangfire;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Expenses;
using Humans.Application.Interfaces.Holded;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Polls Holded for payment status on SepaSent expense reports.
/// Runs every 15 minutes; caps at 50 reports per run (oldest SepaSentAt first).
/// Transitions reports whose Holded purchase document shows PaymentsPending == 0
/// and ApprovedAt != null to the Paid state.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 120)]
public class ExpensePaidPollingJob : IRecurringJob
{
    private const int BatchSize = 50;

    private readonly IExpenseRepository _repo;
    private readonly IHoldedClient _holdedClient;
    private readonly IExpenseReportService _expenseService;
    private readonly IClock _clock;
    private readonly ILogger<ExpensePaidPollingJob> _logger;

    public ExpensePaidPollingJob(
        IExpenseRepository repo,
        IHoldedClient holdedClient,
        IExpenseReportService expenseService,
        IClock clock,
        ILogger<ExpensePaidPollingJob> logger)
    {
        _repo = repo;
        _holdedClient = holdedClient;
        _expenseService = expenseService;
        _clock = clock;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var reports = await _repo.GetByStatusAsync(ExpenseReportStatus.SepaSent, cancellationToken);

        var batch = reports
            .OrderBy(r => r.SepaSentAt ?? r.CreatedAt)
            .Take(BatchSize)
            .ToList();

        if (batch.Count == 0)
            return;

        foreach (var report in batch)
        {
            if (report.HoldedDocId is null)
            {
                _logger.LogWarning(
                    "SepaSent report {ReportId} has no HoldedDocId — skipping",
                    report.Id);
                continue;
            }

            try
            {
                var doc = await _holdedClient.GetPurchaseDocumentAsync(report.HoldedDocId, cancellationToken);

                if (doc.PaymentsPending == 0 && doc.ApprovedAt is not null)
                {
                    await _expenseService.MarkPaidAsync(report.Id, cancellationToken);
                    _logger.LogInformation(
                        "Marked expense report {ReportId} as Paid (HoldedDocId={HoldedDocId})",
                        report.Id, report.HoldedDocId);
                }
            }
            catch (HoldedPermanentException ex) when (ex.StatusCode == 404)
            {
                _logger.LogWarning(
                    "Holded doc {HoldedDocId} for report {ReportId} deleted out-of-band — skipping",
                    report.HoldedDocId, report.Id);
            }
            catch (HoldedTransientException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Transient error polling Holded for report {ReportId} (HoldedDocId={HoldedDocId}) — will retry next run",
                    report.Id, report.HoldedDocId);
            }
        }
    }
}
