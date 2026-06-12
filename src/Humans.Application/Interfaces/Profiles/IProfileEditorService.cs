using Humans.Application.DTOs;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Interfaces.Profiles;

/// <summary>
/// Coordinates profile edit form saves around the Users-owned storage mutation.
/// </summary>
public interface IProfileEditorService : IApplicationService
{
    /// <summary>
    /// Owns the profile-save workflow: validates the cross-field invariants
    /// ("Other" allergy requires free text; Burner CV requires entries or
    /// <c>NoPriorBurnExperience</c> when a CV payload is present — both throw
    /// <c>ValidationException</c>), persists the profile (+ picture + CV), then
    /// runs the after-save domain side effects: the onboarding consent-check
    /// trigger, the initial-setup tier-application submit/draft-update
    /// (<paramref name="tierApplication"/>), and the pending-deletion cancel on
    /// profile creation. Initial setup = no profile yet or not approved,
    /// derived here — not by the caller.
    /// </summary>
    Task<Guid> SaveProfileAsync(
        Guid userId,
        string displayName,
        ProfileSaveRequest request,
        TierApplicationRequest? tierApplication = null,
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
