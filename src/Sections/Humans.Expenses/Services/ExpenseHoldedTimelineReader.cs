using Humans.Base.Interfaces;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Expenses.Domain;
using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Expenses.Services;

internal sealed class ExpenseHoldedTimelineReader(
    IExpenseRepository repo,
    IFinanceService finance,
    IUserService userService,
    IHoldedService holded) : IApplicationService
{
    public async Task<ExpenseHoldedTimeline?> GetHoldedTimelineAsync(
        ExpenseReportDto report, CancellationToken ct = default)
    {
        var outboxEvent = await repo.GetLatestOutboxForReportAsync(report.Id, ct);

        decimal owed = 0m, totalPaid = 0m, memberRegisteredTotal = 0m;
        var paid = false;
        LocalDate? paidOn = null;

        // No contact id means no push has ever linked this member to a Holded creditor, so there is
        // no ledger to read. The push half below still has something to say about why.
        if (!string.IsNullOrEmpty(report.HoldedContactId))
        {
            var status = await finance.GetCreditorStatusAsync(
                report.HoldedSupplierAccountNum, ct);

            // The creditor contact covers every id the human has held, so the total does too.
            var submitter = await userService.GetUserInfoAsync(report.SubmitterUserId, ct);
            foreach (var id in submitter?.AllUserIds ?? [report.SubmitterUserId])
            {
                // Count the documents whatever the report status: reports withdrawn after approval (before that
                // was refused) still have their booking. RegisteredAmount excludes lines not pushed yet.
                memberRegisteredTotal += (await repo.GetForSubmitterAsync(id, ct)).Sum(RegisteredAmount);
            }

            owed = status?.OwedToMember ?? 0m;
            totalPaid = status?.TotalPaid ?? 0m;
            // Settled iff the derived creditor balance (Σdebit − Σcredit) is non-negative. A null status
            // means no cached ledger lines for the account — unknown, not settled.
            paid = status is { } s && s.Balance >= 0m;
            paidOn = status?.LastPaymentDate;
        }

        return new ExpenseHoldedTimeline
        {
            RegisteredInHolded = report.HoldedDocIds.Count > 0,
            OwedToMember = owed,
            MemberRegisteredTotal = memberRegisteredTotal,
            OtherAmount = Math.Max(0m, owed - memberRegisteredTotal),
            Paid = paid,
            PaidOn = paidOn,
            TotalPaid = totalPaid,
            SyncState = ResolveSyncState(outboxEvent),
            QueuedAt = outboxEvent?.OccurredAt,
            SettledAt = outboxEvent?.ProcessedAt,
            RetryCount = outboxEvent?.RetryCount ?? 0,
            MaxRetries = ExpenseReportBackgroundProcessor.MaxOutboxRetries,
            LastError = outboxEvent?.LastError,
            NextRetryAt = outboxEvent is { ProcessedAt: null, FailedPermanently: false }
                ? outboxEvent.NextRetryAt
                : null,
        };
    }

    /// <summary>
    /// A legacy report-level doc carried the whole payable; per-line docs count only lines whose doc exists, so a
    /// half-done push doesn't overstate the registered total against the ledger.
    /// </summary>
    private static decimal RegisteredAmount(ExpenseReportDto report) =>
        report.HoldedDocId is not null
            ? report.Payable
            : PayableAllocation.Allocate(report)
                .Where(a => a.Line.HoldedDocId is not null)
                .Sum(a => a.Booked);

    private ExpenseHoldedSyncState ResolveSyncState(HoldedExpenseOutboxEvent? outboxEvent) =>
        ResolveSyncState(outboxEvent, holded.IsConfigured);

    internal static ExpenseHoldedSyncState ResolveSyncState(
        HoldedExpenseOutboxEvent? outboxEvent, bool holdedConfigured)
    {
        if (outboxEvent is null) return ExpenseHoldedSyncState.NotQueued;
        // Written off sets ProcessedAt too, so it has to be tested before the success case.
        if (outboxEvent.FailedPermanently) return ExpenseHoldedSyncState.Failed;
        if (outboxEvent.ProcessedAt is not null) return ExpenseHoldedSyncState.Pushed;
        if (!holdedConfigured) return ExpenseHoldedSyncState.NotConfigured;
        return outboxEvent.RetryCount > 0
            ? ExpenseHoldedSyncState.Retrying
            : ExpenseHoldedSyncState.Queued;
    }
}
