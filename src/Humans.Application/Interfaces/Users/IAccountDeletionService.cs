using Humans.Application.Interfaces.Onboarding;

namespace Humans.Application.Interfaces.Users;

/// <summary>
/// Sole orchestrator for the user/profile account-deletion cascade. Owns the
/// cross-section write order for every deletion path — user-requested
/// (30-day-scheduled), admin-initiated (immediate purge), and expiry-triggered
/// (scheduled anonymization job). Foundational services (<see cref="IUserService"/>,
/// <see cref="Profiles.IProfileService"/>) keep only own-data deletion; this
/// service invokes them as part of the cascade so the call graph flows in one
/// direction (higher-level → foundational).
/// </summary>
/// <remarks>
/// Extracted in issue #582 (child of umbrella #563) to remove the outbound
/// edges from <c>UserService</c> / <c>ProfileService</c> to higher-level
/// sections (Teams, RoleAssignments, Shifts) and to give
/// <c>ProcessAccountDeletionsJob</c> a single entry point instead of reaching
/// into User/Profile cascade code. Synchronous orchestration (not event-bus):
/// at ~500-user scale, explicit call order is simpler than a pub/sub hop.
/// </remarks>
public interface IAccountDeletionService
{
    /// <summary>
    /// User-initiated account-deletion request. Sets the 30-day scheduled
    /// deletion fields on the user, immediately revokes active team memberships
    /// and governance role assignments (so the user loses access without
    /// waiting for the grace period to expire), writes an audit entry, and
    /// sends the deletion-scheduled confirmation email. No-op with
    /// <c>NotFound</c> if the user does not exist; <c>AlreadyPending</c> if
    /// a deletion request is already open.
    /// </summary>
    Task<OnboardingResult> RequestDeletionAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Admin-initiated purge: anonymizes the identity on the <c>User</c> row
    /// (display name + email replaced with sentinels, <c>UserEmail</c> rows
    /// removed) and invalidates the caches that key off the user's identity
    /// (FullProfile, ActiveTeams) so downstream consumers see the purged view
    /// before TTL expiry. Returns <c>NotFound</c> if the user does not exist.
    /// Used by <c>AdminController.PurgeHuman</c> when an operator removes a
    /// human outside the normal 30-day grace period.
    /// </summary>
    Task<OnboardingResult> PurgeAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Scheduled-job entry point: completes the GDPR expiry cascade for a user
    /// whose 30-day grace period has elapsed. Runs every cross-section cleanup
    /// (team memberships, governance role assignments, profile anonymization
    /// incl. contact fields + volunteer history, shift signup cancellation,
    /// volunteer-event-profile deletion) BEFORE collapsing the User
    /// aggregate's identity — so a mid-cascade failure leaves the deletion
    /// fields intact and tomorrow's job run retries from the same state.
    /// Invalidates the FullProfile, role-assignment claims, shift-authorization,
    /// and Teams member caches. Returns a summary of the pre-anonymization
    /// identity plus the ids of any cancelled shift signups so the caller
    /// (<c>ProcessAccountDeletionsJob</c>) can emit audit entries and send the
    /// confirmation email. Returns <c>null</c> when the user has vanished
    /// between enumeration and cascade start.
    /// </summary>
    Task<AnonymizedAccountSummary?> AnonymizeExpiredAccountAsync(
        Guid userId, CancellationToken ct = default);
}
