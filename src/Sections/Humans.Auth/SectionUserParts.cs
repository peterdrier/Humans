using Humans.Auth.ViewComponents;
using Humans.Base.Interfaces;

namespace Humans.Auth;

internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
        [new(UserPartSlots.AdminDetail, typeof(RoleAssignmentsCardViewComponent), Weight: 50)];
}
