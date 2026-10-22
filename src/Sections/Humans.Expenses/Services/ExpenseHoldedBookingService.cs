using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Users.Contracts;

namespace Humans.Expenses.Services;

internal sealed class ExpenseHoldedBookingService(
    IExpenseRepository repo,
    IUserService userService) : IExpenseHoldedBookingService
{
    public async Task<IReadOnlyList<ExpenseHoldedBooking>> GetHoldedBookingsAsync(CancellationToken ct = default)
    {
        var reports = await repo.GetAllAsync(ct);
        // The creditor is the member as they are now, as the publisher binds it: a since-merged id belongs to the survivor.
        var members = await userService.GetUserInfosAsync(
            reports.Select(r => r.SubmitterUserId).ToHashSet(), ct);
        return reports
            .SelectMany(report => PayableAllocation.Allocate(report)
                .Where(a => a.Line.HoldedDocId is not null)
                .Select(a => new ExpenseHoldedBooking(
                    report.Id,
                    a.Line.Id,
                    members.GetValueOrDefault(report.SubmitterUserId)?.Id ?? report.SubmitterUserId,
                    report.HoldedContactId,
                    a.Line.HoldedDocId!,
                    $"ER: {a.Line.Description}",
                    $"Expense report {report.Id}",
                    a.Booked)))
            .ToList();
    }
}
