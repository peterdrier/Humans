using Humans.Base.Attributes;

namespace Humans.Expenses.Contracts;

public interface IExpenseReportBackgroundProcessor
{
    [ExternalWrite]
    Task DrainHoldedOutboxAsync(int batchSize, CancellationToken ct = default);
}
