using Humans.Base.Attributes;

namespace Humans.Expenses.Services;

internal interface IExpenseReportBackgroundProcessor
{
    [ExternalWrite]
    Task DrainHoldedOutboxAsync(int batchSize, CancellationToken ct = default);
}
