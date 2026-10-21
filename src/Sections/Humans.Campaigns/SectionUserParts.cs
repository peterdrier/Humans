using Humans.Base.Interfaces;
using Humans.Campaigns.ViewComponents;

namespace Humans.Campaigns;

/// <summary>
/// Contributes the member's "My Codes" card to the profile page and the admin "Campaign Codes"
/// card to the admin human-detail page. Discovered by <c>RegisterContributions</c>
/// (parameterless ctor).
/// </summary>
internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
    [
        new(UserPartSlots.Profile, typeof(MyCodesViewComponent), Weight: 100),
        new(UserPartSlots.AdminDetail, typeof(CampaignCodesViewComponent), Weight: 100),
    ];
}
