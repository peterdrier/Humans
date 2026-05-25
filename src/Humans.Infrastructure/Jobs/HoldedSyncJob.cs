using Hangfire;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Finance;

namespace Humans.Infrastructure.Jobs;

/// <summary>Nightly pull of Holded purchase docs → budget-category actuals.</summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class HoldedSyncJob(IHoldedFinanceService finance) : IRecurringJob
{
    public Task ExecuteAsync(CancellationToken cancellationToken = default) =>
        finance.SyncAsync(cancellationToken);
}
