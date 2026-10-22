using Humans.Base.Interfaces;
using Humans.Campaigns.ViewComponents;

namespace Humans.Campaigns;

/// <summary>Discovered by <c>RegisterContributions</c>, so it needs a parameterless ctor.</summary>
internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
    [
        new(UserPartSlots.Profile, typeof(MyCodesViewComponent), Weight: 100),
        new(UserPartSlots.AdminDetail, typeof(CampaignCodesViewComponent), Weight: 100),
    ];
}
