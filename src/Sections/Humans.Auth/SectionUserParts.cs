using Humans.Auth.ViewComponents;
using Humans.Base.Interfaces;

namespace Humans.Auth;

/// <summary>
/// Contributes the "Role Assignments" card to the admin human-detail page. Discovered by
/// <c>RegisterContributions</c> (parameterless ctor).
/// </summary>
internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
        [new(UserPartSlots.AdminDetail, typeof(RoleAssignmentsCardViewComponent), Weight: 50)];
}
