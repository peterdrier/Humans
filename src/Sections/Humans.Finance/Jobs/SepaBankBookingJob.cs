using Hangfire;
using Humans.Base.Interfaces;
using Humans.Finance.Services;

namespace Humans.Finance.Jobs;

[DisableConcurrentExecution(timeoutInSeconds: 600)]
internal sealed class SepaBankBookingJob(IFinanceAdminService finance) : IRecurringJob
{
    public Task ExecuteAsync(CancellationToken cancellationToken = default) =>
        finance.RunSepaBookingSweepAsync(cancellationToken);
}
