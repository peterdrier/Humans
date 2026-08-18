namespace Humans.Base.Models;

/// <summary>
/// One row of a person-lookup JSON response: the id, what to show, an optional disambiguating
/// detail line and an optional avatar. No section vocabulary — it is the shape every people
/// picker in the app returns, which is why it sits in Humans.Base rather than in Shell: a section
/// project cannot reference Humans.Web (design §15 step 6).
/// </summary>
public record HumanLookupSearchResult(
    Guid UserId,
    string DisplayName,
    string? Detail = null,
    string? ProfilePictureUrl = null);
