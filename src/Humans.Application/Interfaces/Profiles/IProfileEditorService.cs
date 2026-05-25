using Humans.Application.DTOs;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Interfaces.Profiles;

/// <summary>
/// Coordinates profile edit form saves around the Users-owned storage mutation.
/// </summary>
public interface IProfileEditorService : IApplicationService
{
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
