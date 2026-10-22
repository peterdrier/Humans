using Hangfire;
using NodaTime;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;

namespace Humans.Users.Jobs;

/// <summary>Suspends members who haven't re-consented to required documents after the grace period.</summary>
/// <remarks>
/// The schedule, metric and failure boundary; the sweep is <see cref="INonCompliantMemberSuspension"/>.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class SuspendNonCompliantMembersJob(
    INonCompliantMemberSuspension suspension,
    IHumansMetrics metrics,
    ILogger<SuspendNonCompliantMembersJob> logger,
    IClock clock) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Starting non-compliant member suspension check at {Time}",
            clock.GetCurrentInstant());

        try
        {
            await suspension.SuspendNonCompliantAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("suspend_noncompliant_members", "failure");
            logger.LogError(ex, "Error checking non-compliant members");
            throw;
        }
    }
}
