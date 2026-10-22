using Humans.Base.Interfaces;
using Humans.Teams.ViewComponents;

namespace Humans.Teams;

internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
    [
        new(UserPartSlots.Profile, typeof(TeamMessageButtonsViewComponent), Weight: 200),
    ];
}
