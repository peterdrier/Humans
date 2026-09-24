using Humans.Base.Interfaces;
using Humans.Tickets.ViewComponents;

namespace Humans.Tickets;

/// <summary>
/// Contributes <see cref="TicketHoldingsViewComponent"/> to the profile and admin-detail
/// sidebars (nobodies-collective/Humans#1815). Discovered by <c>RegisterContributions</c>
/// (parameterless ctor).
/// </summary>
internal sealed class SectionUserParts : IUserPart
{
    public IEnumerable<UserPart> Parts() =>
    [
        new(UserPartSlots.ProfileSidebar, typeof(TicketHoldingsViewComponent)),
        new(UserPartSlots.AdminDetailSidebar, typeof(TicketHoldingsViewComponent)),
    ];
}
