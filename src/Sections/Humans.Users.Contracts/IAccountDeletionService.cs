using Humans.Onboarding.Contracts;
using NodaTime;

namespace Humans.Users.Contracts;

/// <summary>
/// Owns the cross-section write order of every deletion path; foundational services only delete their own
/// data, and this calls them, so the call graph flows one way.
/// </summary>
public interface IAccountDeletionService
{
    /// <summary>
    /// Schedules deletion in 30 days (held past the event for a ticket holder) and revokes team memberships and
    /// role assignments at once, so access goes without waiting out the grace period. Audits and emails.
    /// <c>NotFound</c>, or <c>AlreadyPending</c> when a request is open.
    /// </summary>
    Task<DeletionRequestResult> RequestDeletionAsync(Guid userId, CancellationToken ct = default);

    /// <summary><c>NotFound</c>, or <c>NoDeletionPending</c> when no request is open.</summary>
    Task<OnboardingResult> CancelDeletionAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Immediate anonymization outside the grace period, with caches invalidated and an
    /// <c>AccountPurged</c> audit entry keyed by <paramref name="actorId"/>. <c>NotFound</c> if the user doesn't exist.
    /// </summary>
    Task<OnboardingResult> PurgeAsync(Guid userId, Guid? actorId = null, CancellationToken ct = default);

    /// <summary>
    /// Runs every cross-section cleanup before collapsing the identity, so a mid-cascade failure leaves the
    /// deletion fields intact and the next day's run retries. Returns null when the user vanished meanwhile.
    /// </summary>
    Task<AnonymizedAccountSummary?> AnonymizeExpiredAccountAsync(
        Guid userId, CancellationToken ct = default);
}

/// <summary>
/// On success, <see cref="EffectiveDeletionDate"/> is the later of the scheduled date and any ticket-holder floor,
/// and <see cref="IsHeldForTicket"/> says which copy to render. <c>ErrorKey</c>: <c>NotFound</c>, <c>AlreadyPending</c>.
/// </summary>
public sealed record DeletionRequestResult(
    bool Success,
    string? ErrorKey = null,
    Instant? EffectiveDeletionDate = null,
    bool IsHeldForTicket = false);
