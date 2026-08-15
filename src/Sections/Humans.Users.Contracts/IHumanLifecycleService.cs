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
// COVERAGE REDUCED (G5 lane 3b, nobodies-collective/Humans#866): dropped ": IOrchestrator".
// The marker lives in Humans.Interfaces and this leaf must reach zero <ProjectReference>.
// Lost on the implementing class: HUM0026 (orchestrator injects a repository/DbContext) and
// HUM0027 (role-axis exclusivity) — OrchestratorRepositoryInjectionAnalyzer keys on IOrchestrator,
// so it now sees nothing here. Restore when Base is referenceable from this leaf again.
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
