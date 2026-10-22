
namespace Humans.Users.Contracts;

public interface IProfileEditorService
{
    /// <summary>
    /// Backstops the cross-field rules (a CV payload needs entries or <c>NoPriorBurnExperience</c>) with a refusal
    /// before any write. Cross-section after-save effects belong to the controller, never here.
    /// </summary>
    Task<ProfileEditResult> SaveProfileAsync(
        Guid userId,
        ProfileSaveRequest request,
        CancellationToken ct = default);
}

public sealed record ProfileEditResult(bool Succeeded, Guid? ProfileId = null, string? ErrorKey = null);
