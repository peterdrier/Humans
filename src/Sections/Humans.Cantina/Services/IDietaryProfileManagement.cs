using Humans.Cantina.Contracts;

namespace Humans.Cantina.Services;

/// <summary>Cantina-internal surface over <c>cantina_dietary_profiles</c>: the member's own save.</summary>
internal interface IDietaryProfileManagement : ICantinaService
{
    /// <summary>Saves the member's food answers. Unknown chips are dropped, "Other" text is kept only
    /// with "Other" selected, blanks become null. Returns a resource key when "Other" is selected
    /// without text (nothing is written), null on success.</summary>
    Task<string?> SaveAsync(Guid userId, DietaryProfileInfo answers, CancellationToken ct = default);

}
