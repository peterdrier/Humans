using Humans.Onboarding.Contracts;

namespace Humans.Users.Contracts;

/// <summary>
/// Owns ongoing-membership state transitions on already-onboarded humans
/// (suspend / unsuspend, and — over time — re-consent suspensions, status
/// recomputes triggered by external events, and term-renewal flows). This
/// service is the lifecycle state-machine counterpart to
/// <c>IOnboardingService</c> (the intake funnel) and the future
/// account-deletion service (the cascade). All three were originally
/// bundled into <c>OnboardingService</c>; the split is by mission and
/// workflow stage, not by dependency shape (see umbrella issue
/// nobodies-collective#563).
/// </summary>
// The IOrchestrator marker lives on HumanLifecycleService, not here: this leaf must reach zero
// <ProjectReference> and the analyzers (HUM0026, HUM0027) key on the implementing class anyway.
public interface IHumanLifecycleService
{
    /// <summary>
    /// Suspends a human (admin-initiated). Sets <c>State = AdminSuspended</c> on
    /// the profile, records suspension audit metadata, dispatches an
    /// <c>AccessSuspended</c> notification, and increments the
    /// <c>members_suspended{source="admin"}</c> metric.
    /// </summary>
    Task<OnboardingResult> SuspendAsync(
        Guid userId, Guid adminId, string? notes, CancellationToken ct = default);

    /// <summary>
    /// Unsuspends a human (admin-initiated). Clears the suspended
    /// <c>State</c> on the profile and resolves any open <c>AccessSuspended</c>
    /// notifications in the user's inbox.
    /// </summary>
    Task<OnboardingResult> UnsuspendAsync(
        Guid userId, Guid adminId, CancellationToken ct = default);

    /// <summary>
    /// Restores access after a consent-suspended human completes all required
    /// consents. Does nothing for <c>AdminSuspended</c> users.
    /// </summary>
    Task<OnboardingResult> RestoreConsentSuspensionAsync(
        Guid userId, CancellationToken ct = default);
}
