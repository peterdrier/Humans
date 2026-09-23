using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.Tour.Tests;

/// <summary>The member's entry point: a dashboard card the section contributes itself.</summary>
public class SectionMemberDashboardTests
{
    [HumansFact]
    public void Contributes_the_Tour_card_to_the_member_dashboard()
    {
        var component = new SectionMemberDashboard().Components().Should().ContainSingle().Subject;

        component.Slot.Should().Be(ChromeSlots.MemberDashboard);
    }
}
