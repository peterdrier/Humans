using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Holded.Contracts;
using Humans.Users.Contracts;

namespace Humans.Expenses.Services;

internal sealed class ExpenseHoldedBookingService(
    IExpenseRepository repo,
    IUserService userService,
    IHoldedClient holdedClient) : IExpenseHoldedBookingService
{
    public async Task<IReadOnlyList<ExpenseHoldedBooking>> GetHoldedBookingsAsync(CancellationToken ct = default)
    {
        var reports = await repo.GetAllAsync(ct);
        var creditors = await CreditorsAsync(reports, ct);
        return reports
            .SelectMany(report => PayableAllocation.Allocate(report)
                .Where(a => a.Line.HoldedDocId is not null)
                .Select(a => new ExpenseHoldedBooking(
                    report.Id,
                    a.Line.Id,
                    creditors(report.SubmitterUserId),
                    report.HoldedContactId,
                    a.Line.HoldedDocId!,
                    ExpenseHoldedPublisher.DocDescription(a.Line.Description),
                    ExpenseHoldedPublisher.DocNotes(report.Id),
                    a.Booked)))
            .ToList();
    }

    public async Task<IReadOnlyList<ExpenseHoldedReconLine>> GetHoldedReconLinesAsync(CancellationToken ct = default)
    {
        var reports = await repo.GetAllAsync(ct);
        var creditors = await CreditorsAsync(reports, ct);
        var outbox = await repo.GetLatestOutboxByReportAsync(ct);
        var configured = holdedClient.IsConfigured;

        var lines = new List<ExpenseHoldedReconLine>();
        foreach (var report in reports)
        {
            var approved = report.Status == ExpenseReportStatus.Approved;
            var outboxEvent = outbox.GetValueOrDefault(report.Id);
            ExpenseHoldedReconLine Line(Guid? lineId, string? docId, string description, decimal booked) => new(
                report.Id, lineId, report.Status, creditors(report.SubmitterUserId), report.HoldedContactId, docId,
                description, ExpenseHoldedPublisher.DocNotes(report.Id), booked,
                report.SubmittedAt ?? report.CreatedAt,
                ExpenseReportService.ResolveSyncState(outboxEvent, configured),
                outboxEvent?.ProcessedAt);

            // A legacy push wrote one document for the whole payable and left the lines unlinked.
            if (report.HoldedDocId is not null)
            {
                lines.Add(Line(null, report.HoldedDocId, "", report.Payable));
                continue;
            }

            lines.AddRange(PayableAllocation.Allocate(report)
                .Where(a => a.Booked > 0m && (approved || a.Line.HoldedDocId is not null))
                .Select(a => Line(
                    a.Line.Id, a.Line.HoldedDocId,
                    ExpenseHoldedPublisher.DocDescription(a.Line.Description), a.Booked)));
        }
        return lines;
    }

    /// <summary>The creditor is the member as they are now, as the publisher binds it: a since-merged id belongs to the
    /// survivor.</summary>
    private async Task<Func<Guid, Guid>> CreditorsAsync(
        IReadOnlyList<ExpenseReportDto> reports, CancellationToken ct)
    {
        var members = await userService.GetUserInfosAsync(
            reports.Select(r => r.SubmitterUserId).ToHashSet(), ct);
        return id => members.GetValueOrDefault(id)?.Id ?? id;
    }
}
