using Hangfire;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;

namespace Humans.Finance.Jobs;

/// <summary>The SEPA bank-line sweep: book the transfers whose Sabadell line has landed, retry the
/// pending reconciles (nobodies-collective/Humans#1185). The schedule lives in
/// <c>SectionJobs.cs</c>.</summary>
/// <remarks>
/// A shim, not the body — the body is <see cref="ISepaBankBooking"/>, implemented by this section.
/// It is public, and stays public, because Hangfire needs the concrete type at registration;
/// HUM0034 makes every other public type in a section an error.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 600)]
public class SepaBankBookingJob(ISepaBankBooking sweep) : IRecurringJob
{
    public Task ExecuteAsync(CancellationToken cancellationToken = default) =>
        sweep.RunAsync(cancellationToken);
}
