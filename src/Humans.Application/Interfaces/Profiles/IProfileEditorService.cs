using Humans.Application.DTOs;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Interfaces.Profiles;

/// <summary>
/// Coordinates profile edit form saves around the Users-owned storage mutation.
/// </summary>
public interface IProfileEditorService : IApplicationService
{
    /// <summary>
    /// Validates the cross-field invariants as a service-side backstop ("Other"
    /// allergy requires free text; Burner CV requires entries or
    /// <c>NoPriorBurnExperience</c> when a CV payload is present — both throw
    /// <c>ValidationException</c>), then persists the profile (+ picture + CV).
    /// Cross-section after-save effects (onboarding consent-check trigger,
    /// initial-setup tier application, pending-deletion cancel) are deliberately
    /// NOT here — they are controller-orchestrated per
    /// <c>memory/architecture/no-leaf-to-director-callbacks.md</c> and
    /// <c>memory/architecture/user-profile-foundational.md</c>.
    /// </summary>
    Task<Guid> SaveProfileAsync(
        Guid userId,
        string displayName,
        ProfileSaveRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Persists the full dietary + medical set (the DietaryMedical page). Updates
    /// only those six Profile columns. Caller must have verified ownership/authorization
    /// (MedicalConditions is GDPR Art. 9).
    /// </summary>
    Task SaveDietaryMedicalAsync(
        Guid userId,
        UserProfileDietaryMedicalCommand command,
        CancellationToken ct = default);
}
