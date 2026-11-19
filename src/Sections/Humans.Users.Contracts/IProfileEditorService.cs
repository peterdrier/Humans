
namespace Humans.Users.Contracts;

public interface IProfileEditorService
{
    /// <summary>
    /// Backstops the cross-field rules (a CV payload needs entries or <c>NoPriorBurnExperience</c>) with a refusal
    /// before any write. Optional contacts and languages replace those collections; null leaves them unchanged.
    /// Cross-section after-save effects belong to the controller, never here.
    /// </summary>
    Task<ProfileEditResult> SaveProfileAsync(
        Guid userId,
        ProfileSaveRequest request,
        CancellationToken ct = default,
        IReadOnlyList<ContactFieldEditDto>? contactFields = null,
        IReadOnlyList<ProfileLanguageInfo>? languages = null,
        IReadOnlyCollection<Guid>? removedLegacyContactFieldIds = null);
}

public sealed record ProfileEditResult(bool Succeeded, Guid? ProfileId = null, string? ErrorKey = null,
    string? ErrorField = null, string? ErrorArgumentKey = null);
