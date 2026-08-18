namespace Humans.Expenses.Contracts;

public interface IExpenseReportBackgroundProcessor
{
    /// <summary>
    /// Drains the Holded expense outbox: creates or updates purchase documents in Holded
    /// for each approved expense report.
    /// </summary>
    Task DrainHoldedOutboxAsync(int batchSize, CancellationToken ct = default);
}
