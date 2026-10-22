namespace Humans.Base.Enums;

/// <summary>
/// Audience the profile card renders for. In Base rather than beside <c>ProfileCardViewComponent</c> because it is
/// part of the user-part slot contract (<see cref="Humans.Base.Interfaces.UserPartArgs"/>), which every host and
/// contributing section must be able to name.
/// </summary>
public enum ProfileCardViewMode
{
    Self,
    Public,
    Admin
}
