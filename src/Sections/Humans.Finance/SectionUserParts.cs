using Humans.Base.Interfaces;
using Humans.Finance.ViewComponents;

namespace Humans.Finance;

/// <summary>Discovered by <c>RegisterContributions</c>, so it needs a parameterless ctor.</summary>
internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
        [new(UserPartSlots.AdminDetailSidebar, typeof(PaymentDetailsViewComponent), Weight: 100)];
}
