using Humans.Base.Interfaces;
using Humans.Teams.ViewComponents;

namespace Humans.Teams;

/// <summary>
/// Contributes the "send a message from {team}" buttons to the profile page.
/// Discovered by <c>RegisterContributions</c> (parameterless ctor).
/// </summary>
internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
    [
        new(UserPartSlots.Profile, typeof(TeamMessageButtonsViewComponent), Weight: 200),
    ];
}
