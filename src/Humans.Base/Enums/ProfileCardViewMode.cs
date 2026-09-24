namespace Humans.Base.Enums;

/// <summary>
/// Audience the profile card renders for. Lives in <c>Humans.Base.Enums</c> rather than beside
/// <c>ProfileCardViewComponent</c> because it is part of the user-part slot contract
/// (<see cref="Humans.Base.Interfaces.UserPartArgs"/>), which every host and contributing
/// section must be able to name. The enum carries no section vocabulary, the same test the
/// API-key filter base and <c>HumanLookupSearchResult</c> passed.
/// </summary>
public enum ProfileCardViewMode
{
    Self,
    Public,
    Admin
}
