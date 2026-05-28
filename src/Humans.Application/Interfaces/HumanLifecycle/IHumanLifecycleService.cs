using Humans.Application.Interfaces.Onboarding;

namespace Humans.Application.Interfaces.HumanLifecycle;

/// <summary>
/// Owns ongoing-membership state transitions on already-onboarded humans
/// (suspend / unsuspend, and — over time — re-consent suspensions, status
/// recomputes triggered by external events, and term-renewal flows). This
/// service is the lifecycle state-machine counterpart to
/// <see cref="IOnboardingService"/> (the intake funnel) and the future
/// account-deletion service (the cascade). All three were originally
/// bundled into <c>OnboardingService</c>; the split is by mission and
/// workflow stage, not by dependency shape (see umbrella issue
/// nobodies-collective#563).
/// </summary>
public interface IHumanLifecycleService : IOrchestrator
{
    /// <summary>
    /// Suspends a human (admin-initiated). Sets <c>IsSuspended = true</c> on
    /// the profile, records suspension audit metadata, dispatches an
    /// <c>AccessSuspended</c> notification, and increments the
    /// <c>members_suspended{source="admin"}</c> metric.
    /// </summary>
    Task<OnboardingResult> SuspendAsync(
        Guid userId, Guid adminId, string? notes, CancellationToken ct = default);

    /// <summary>
    /// Unsuspends a human (admin-initiated). Sets <c>IsSuspended = false</c>
    /// on the profile and resolves any open <c>AccessSuspended</c>
    /// notifications in the user's inbox.
    /// </summary>
    Task<OnboardingResult> UnsuspendAsync(
        Guid userId, Guid adminId, CancellationToken ct = default);
}
