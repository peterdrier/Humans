namespace Humans.Base.Enums;

/// <summary>
/// Audience the profile card renders for. Lives in <c>Humans.Base.Enums</c> rather than beside
/// <c>ProfileCardViewComponent</c> in Shell because a section that invokes the card by name
/// — <c>Component.InvokeAsync("ProfileCard", new { userId, viewMode })</c> — has to name the
/// argument's type, and a section cannot reference <c>Humans.Web</c> (design §15 step 6). The
/// enum carries no section vocabulary, the same test the API-key filter base and
/// <c>HumanLookupSearchResult</c> passed.
/// </summary>
public enum ProfileCardViewMode
{
    Self,
    Public,
    Admin
}
