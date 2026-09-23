using Humans.Base.Interfaces;
using Humans.Tickets.ViewComponents;

namespace Humans.Tickets;

/// <summary>
/// Tickets' dashboard content: the member ticket card, and the order summary on the
/// profileless-account page.
/// </summary>
internal sealed class SectionMemberDashboard : ISectionMemberDashboard
{
    public IEnumerable<ChromeComponent> Components() =>
    [
        new ChromeComponent(ChromeSlots.MemberDashboard, typeof(MemberTicketStatusViewComponent), Weight: 20),
        new ChromeComponent(ChromeSlots.GuestPage, typeof(GuestTicketOrdersViewComponent))
    ];
}
