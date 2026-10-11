
namespace Humans.Users.Contracts;

/// <summary>
/// Coordinates profile edit form saves around the Users-owned storage mutation.
/// </summary>
// No marker interface — see Humans.Users.Contracts.csproj.
public interface IProfileEditorService
{
    /// <summary>
    /// Validates the cross-field invariants as a service-side backstop ("Other"
    /// allergy requires free text; Burner CV requires entries or
    /// <c>NoPriorBurnExperience</c> when a CV payload is present), returning an
    /// ordinary refusal before persistence or the saved profile id (+ picture + CV).
    /// Cross-section after-save effects (onboarding consent-check trigger,
    /// initial-setup tier application, pending-deletion cancel) are deliberately
    /// NOT here — they are controller-orchestrated per
    /// <c>memory/architecture/no-leaf-to-director-callbacks.md</c> and
    /// <c>memory/architecture/user-profile-foundational.md</c>.
    /// </summary>
    Task<ProfileEditResult> SaveProfileAsync(
        Guid userId,
        string displayName,
        ProfileSaveRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Persists the full dietary + medical set (the DietaryMedical page). Updates
    /// only those six Profile columns. Caller must have verified ownership/authorization
    /// (MedicalConditions is GDPR Art. 9).
    /// </summary>
    Task<ProfileEditResult> SaveDietaryMedicalAsync(
        Guid userId,
        UserProfileDietaryMedicalCommand command,
        CancellationToken ct = default);
}

/// <summary>A completed profile edit, or a cross-field refusal before persistence.</summary>
public sealed record ProfileEditResult(bool Succeeded, Guid? ProfileId = null, string? ErrorKey = null);
