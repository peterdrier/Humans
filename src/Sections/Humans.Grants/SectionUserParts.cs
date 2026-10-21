using Humans.Base.Interfaces;
using Humans.Grants.ViewComponents;

namespace Humans.Grants;

/// <summary>
/// Contributes the member's "My grants" card to the profile page and the admin "Grants" card
/// to the admin human-detail page. Discovered by <c>RegisterContributions</c>
/// (parameterless ctor).
/// </summary>
internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
    [
        new(UserPartSlots.Profile, typeof(MyGrantsViewComponent), Weight: 95),
        new(UserPartSlots.AdminDetail, typeof(UserGrantsViewComponent), Weight: 95),
    ];
}
