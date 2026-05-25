using Hangfire;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Finance;

namespace Humans.Infrastructure.Jobs;

/// <summary>Nightly Holded pull: purchase docs → budget-category actuals, plus creditor balances + payments.</summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class HoldedSyncJob(IHoldedFinanceService finance) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await finance.SyncAsync(cancellationToken);
        await finance.SyncCreditorDataAsync(cancellationToken);
    }
}
