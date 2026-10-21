using Humans.Base.Interfaces;
using Humans.Finance.ViewComponents;

namespace Humans.Finance;

/// <summary>
/// Contributes <see cref="PaymentDetailsViewComponent"/> to the admin human-detail sidebar, after
/// the other sidebar parts. Discovered by <c>RegisterContributions</c> (parameterless ctor).
/// </summary>
internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
        [new(UserPartSlots.AdminDetailSidebar, typeof(PaymentDetailsViewComponent), Weight: 100)];
}
