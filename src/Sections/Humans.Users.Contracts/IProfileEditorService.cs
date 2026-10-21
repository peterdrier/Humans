
namespace Humans.Users.Contracts;

/// <summary>
/// Coordinates profile edit form saves around the Users-owned storage mutation.
/// </summary>
// No marker interface — see Humans.Users.Contracts.csproj.
public interface IProfileEditorService
{
    /// <summary>
    /// Validates the cross-field invariants as a service-side backstop (Burner CV
    /// requires entries or <c>NoPriorBurnExperience</c> when a CV payload is present), returning an
    /// ordinary refusal before persistence or the saved profile id (+ picture + CV).
    /// Cross-section after-save effects (onboarding consent-check trigger,
    /// initial-setup tier application, pending-deletion cancel) are deliberately
    /// NOT here — they are controller-orchestrated per
    /// <c>memory/architecture/no-leaf-to-director-callbacks.md</c> and
    /// <c>memory/architecture/user-profile-foundational.md</c>.
    /// </summary>
    Task<ProfileEditResult> SaveProfileAsync(
        Guid userId,
        ProfileSaveRequest request,
        CancellationToken ct = default);
}

/// <summary>A completed profile edit, or a cross-field refusal before persistence.</summary>
public sealed record ProfileEditResult(bool Succeeded, Guid? ProfileId = null, string? ErrorKey = null);
