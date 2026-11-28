using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.Tickets.Tests;

public sealed class SectionUserPartsTests
{
    [HumansFact]
    public void Parts_Section_ContributesTicketSidebars()
    {
        IUserPart contribution = new Section();

        var parts = contribution.Parts();

        parts.Select(p => (p.Slot, p.Component.Name)).Should().BeEquivalentTo(
        [
            (UserPartSlots.ProfileSidebar, "TicketHoldingsViewComponent"),
            (UserPartSlots.AdminDetailSidebar, "TicketHoldingsViewComponent"),
            (UserPartSlots.ProfileSidebar, "IssuedTicketsViewComponent"),
            (UserPartSlots.AdminDetailSidebar, "IssuedTicketsViewComponent"),
            (UserPartSlots.Profile, "OnsiteChipViewComponent"),
        ]);
    }
}
