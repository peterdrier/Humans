using Humans.Application.Interfaces;

namespace Humans.Users.Contracts;

/// <summary>
/// The nightly non-compliance sweep: suspends members whose required document
/// consents are missing past the grace period, and performs the downstream
/// side effects of each suspension (email, in-app notification, Google team
/// resource removal, audit entry, claims/authorization cache eviction).
/// </summary>
/// <remarks>
/// Carved out of <c>Humans.Infrastructure.Jobs.SuspendNonCompliantMembersJob</c>
/// at G5 lane 4b-2d. The job class itself stays in <c>Humans.Infrastructure</c>
/// and is now a shim: Hangfire serializes the *declaring type name* of a
/// recurring job target, so <c>RecurringJob.AddOrUpdate&lt;SuspendNonCompliantMembersJob&gt;</c>
/// pins that type to its assembly and namespace — a job queued or retry-delayed
/// before a deploy that moved it could not resolve its target afterwards, and
/// nothing in the build or the test suite would say so (batch #4 finding 31).
/// The implementation is resolved from DI at execution time, so it is free to
/// live inside the section that owns the membership lifecycle
/// (Peter, 2026-08-14: <c>SuspendNonCompliantMembersJob</c> → Users).
///
/// The contract is "do the thing", never "give me the rows" (design §15 step 6b):
/// the old job body reached across seven sections' contracts from Base.
/// </remarks>
public interface INonCompliantMemberSuspension : IOrchestrator
{
    /// <summary>
    /// Suspends every member who is now non-compliant and runs each suspension's
    /// downstream side effects. Best-effort per user: an email, notification or
    /// Google-sync failure is logged and does not abort the sweep.
    /// </summary>
    Task SuspendNonCompliantAsync(CancellationToken cancellationToken = default);
}
