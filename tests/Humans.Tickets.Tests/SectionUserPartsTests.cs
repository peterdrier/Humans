using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.Tickets.Tests;

public sealed class SectionUserPartsTests
{
    [HumansFact]
    public void Parts_ContributesBothTicketHoldingsSidebars_AndTheOnsiteChip()
    {
        IUserPart contribution = new SectionUserParts();

        var parts = contribution.Parts();

        parts.Select(p => p.Slot).Should().BeEquivalentTo(
        [
            UserPartSlots.ProfileSidebar,
            UserPartSlots.AdminDetailSidebar,
            UserPartSlots.Profile,
        ]);
    }
}
