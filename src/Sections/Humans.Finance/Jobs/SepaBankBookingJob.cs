using Hangfire;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;

namespace Humans.Finance.Jobs;

[DisableConcurrentExecution(timeoutInSeconds: 600)]
public class SepaBankBookingJob(ISepaBankBooking sweep) : IRecurringJob
{
    public Task ExecuteAsync(CancellationToken cancellationToken = default) =>
        sweep.RunAsync(cancellationToken);
}
