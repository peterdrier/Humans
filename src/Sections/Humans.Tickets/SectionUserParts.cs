using Humans.Base.Interfaces;
using Humans.Tickets.ViewComponents;

namespace Humans.Tickets;

internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
    [
        new(UserPartSlots.Profile, typeof(OnsiteChipViewComponent), Weight: 10),
        new(UserPartSlots.ProfileSidebar, typeof(TicketHoldingsViewComponent)),
        new(UserPartSlots.AdminDetailSidebar, typeof(TicketHoldingsViewComponent)),
    ];
}
