
namespace Humans.Users.Contracts;

/// <summary>
/// The nightly non-compliance sweep: suspends members whose required document
/// consents are missing past the grace period, and performs the downstream
/// side effects of each suspension (email, in-app notification, Google team
/// resource removal, audit entry, claims/authorization cache eviction).
/// </summary>
/// <remarks>
/// Carved out of <c>SuspendNonCompliantMembersJob</c> at G5 lane 4b-2d, when the job class
/// was believed to be pinned to <c>Humans.Infrastructure</c> by Hangfire. That premise was
/// re-measured and found false — <c>RecurringJob.AddOrUpdate&lt;T&gt;(id, …)</c> is keyed on
/// the job id and rewrites the stored type string at every boot — so the job followed its
/// body into this section at G5 lane 5b-4 (nobodies-collective/Humans#866) and now sits at
/// <c>Humans.Users/Contracts/SuspendNonCompliantMembersJob.cs</c>
/// (Peter, 2026-08-14: <c>SuspendNonCompliantMembersJob</c> → Users).
///
/// With both halves in one assembly this interface has no consumer outside the section. It
/// is kept rather than folded into the job, matching lane 5b-1's open item 24 on the four
/// leaf interfaces its jobs left behind: shrinking interface surface needs Peter.
///
/// The contract is "do the thing", never "give me the rows" (design §15 step 6b):
/// the old job body reached across seven sections' contracts from Base.
/// </remarks>
// COVERAGE REDUCED (G5 lane 3b, nobodies-collective/Humans#866): dropped ": IOrchestrator".
// Lost on the implementing class: HUM0026 and HUM0027. See Humans.Users.Contracts.csproj.
public interface INonCompliantMemberSuspension
{
    /// <summary>
    /// Suspends every member who is now non-compliant and runs each suspension's
    /// downstream side effects. Best-effort per user: an email, notification or
    /// Google-sync failure is logged and does not abort the sweep.
    /// </summary>
    Task SuspendNonCompliantAsync(CancellationToken cancellationToken = default);
}
