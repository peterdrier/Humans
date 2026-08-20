using Hangfire;
using NodaTime;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;

namespace Humans.Users.Jobs;

/// <summary>
/// Background job that suspends members who haven't re-consented to required documents
/// after the grace period has expired.
/// </summary>
/// <remarks>
/// The schedule, the metric and the failure boundary; the sweep itself is
/// <see cref="INonCompliantMemberSuspension"/>, carved into this section at G5 lane 4b-2d
/// (Peter, 2026-08-14: membership lifecycle is Users, not Governance). Both halves are now
/// in the same assembly — the interface is kept rather than folded in, matching lane 5b-1's
/// call on the leaf interfaces its jobs left behind (open item 24: leave them public).
///
/// Moved out of <c>Humans.Infrastructure/Jobs</c> at G5 lane 5b-4
/// (nobodies-collective/Humans#866), which retired the "this type must not move" note the
/// carve left here: <c>RecurringJob.AddOrUpdate&lt;T&gt;(id, …)</c> is keyed on the job
/// <em>id</em> and rewrites the stored type string at every boot, so the type is free to
/// change assembly. It sits under <c>Jobs/</c> because Shell names the concrete type
/// at registration and HUM0034 makes every other public type in a section assembly an error.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class SuspendNonCompliantMembersJob(
    INonCompliantMemberSuspension suspension,
    IHumansMetrics metrics,
    ILogger<SuspendNonCompliantMembersJob> logger,
    IClock clock) : IRecurringJob
{
    /// <summary>
    /// Checks and updates membership status for users missing required consents past grace period.
    /// </summary>
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
