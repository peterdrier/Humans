using Humans.Onboarding.Contracts;

namespace Humans.Users.Contracts;

/// <summary>The state machine for already-onboarded humans, beside the intake funnel and the deletion cascade.</summary>
public interface IHumanLifecycleService
{
    /// <summary>
    /// Sets <c>AdminSuspended</c>, records the audit metadata, sends an <c>AccessSuspended</c> notification and
    /// counts it in <c>members_suspended{source="admin"}</c>.
    /// </summary>
    Task<OnboardingResult> SuspendAsync(
        Guid userId, Guid adminId, string? notes, CancellationToken ct = default);

    /// <summary>Also resolves open <c>AccessSuspended</c> notifications.</summary>
    Task<OnboardingResult> UnsuspendAsync(
        Guid userId, Guid adminId, CancellationToken ct = default);

    /// <summary>After a consent-suspended human completes all required consents. No-op for <c>AdminSuspended</c>.</summary>
    Task<OnboardingResult> RestoreConsentSuspensionAsync(
        Guid userId, CancellationToken ct = default);
}
