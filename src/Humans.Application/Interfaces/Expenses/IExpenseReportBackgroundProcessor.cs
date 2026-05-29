namespace Humans.Application.Interfaces.Expenses;

public interface IExpenseReportBackgroundProcessor
{
    /// <summary>
    /// Drains the Holded expense outbox: creates or updates purchase documents in Holded
    /// for each approved expense report.
    /// </summary>
    Task DrainHoldedOutboxAsync(int batchSize, CancellationToken ct = default);

    /// <summary>
    /// Reconciles payment status on SepaSent expense reports against the member's Holded creditor
    /// balance and marks them Paid when that balance is settled.
    /// </summary>
    Task PollHoldedPaidStatusAsync(int batchSize, CancellationToken ct = default);
}
