using Humans.Cantina.Contracts;

namespace Humans.Cantina.Services;

/// <summary>Cantina-internal surface over <c>cantina_dietary_profiles</c>: the member's own save or
/// decline and the legacy copy behind <c>cantina.profile-dietary</c>.</summary>
internal interface IDietaryProfileManagement : ICantinaService
{
    /// <summary>Saves the member's food answers. Unknown chips are dropped, "Other" text is kept only
    /// with "Other" selected, blanks become null. Returns a resource key when "Other" is selected
    /// without text (nothing is written), null on success.</summary>
    Task<string?> SaveAsync(Guid userId, DietaryProfileInfo answers, CancellationToken ct = default);

    /// <summary>Records "I don't want to answer": clears every food field and stops the dietary
    /// prompts until the member saves answers.</summary>
    Task DeclineAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Members whose legacy <c>profiles</c> food columns hold something and who have no
    /// <c>cantina_dietary_profiles</c> row yet, oldest-joined first.</summary>
    Task<IReadOnlyList<DietaryProfileInfo>> GetUncopiedLegacyProfilesAsync(CancellationToken ct = default);

    /// <summary>Copies every pending legacy profile into its own row. Idempotent: a row written since
    /// the list was read is left alone. Returns the rows written.</summary>
    Task<int> CopyLegacyProfilesAsync(CancellationToken ct = default);
}
