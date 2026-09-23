using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.Events.Tests;

public sealed class SectionUserPartTests
{
    [HumansFact]
    public void Parts_ContributesToTheProfileSlot()
    {
        IUserPart contribution = new Section();

        var parts = contribution.Parts();

        parts.Should().ContainSingle().Which.Slot.Should().Be(UserPartSlots.Profile);
    }
}
