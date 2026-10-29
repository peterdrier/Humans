using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.Calendar.Tests;

public sealed class SectionUserPartsTests
{
    [HumansFact]
    public void Parts_ContributesToTheAdminDetailSlot()
    {
        IUserPart contribution = ((IUserPart)new Section());

        var parts = contribution.Parts();

        parts.Should().ContainSingle().Which.Slot.Should().Be(UserPartSlots.AdminDetail);
    }
}
