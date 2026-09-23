using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Events.ViewComponents;

namespace Humans.Events.Tests;

public sealed class SectionUserPartTests
{
    [HumansFact]
    public void Parts_ContributesEventsCardForProfileTarget()
    {
        IUserPart contribution = new Section();

        var parts = contribution.Parts();

        parts.Should().ContainSingle().Which.Should().Be(new UserPart(typeof(EventsCardViewComponent)));
    }
}
