using Hangfire;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Users.Contracts;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that suspends members who haven't re-consented to required documents
/// after the grace period has expired.
/// </summary>
/// <remarks>
/// A shim. The body moved into <c>Humans.Users</c> at G5 lane 4b-2d (Peter, 2026-08-14:
/// membership lifecycle is Users, not Governance) and is reached through
/// <see cref="INonCompliantMemberSuspension"/>.
///
/// <b>This type must not move, be renamed, or change namespace.</b> Hangfire serializes the
/// declaring type name of a recurring job target, and
/// <c>RecurringJob.AddOrUpdate&lt;SuspendNonCompliantMembersJob&gt;</c> in
/// <c>RecurringJobExtensions</c> pins it to this assembly and namespace. A job enqueued or
/// retry-delayed before a deploy that relocated it would fail to resolve its target
/// afterwards, and neither the build nor the test suite would report it
/// (G5 batch #4 finding 31). Carving the body out is safe by construction: the serialized
/// type never moves and the implementation is resolved from DI at execution time.
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
