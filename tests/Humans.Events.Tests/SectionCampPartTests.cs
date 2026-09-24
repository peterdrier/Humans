using AwesomeAssertions;
using Humans.Camps.Contracts;
using Humans.Events.ViewComponents;

namespace Humans.Events.Tests;

public sealed class SectionCampPartTests
{
    [HumansFact]
    public void Parts_ContributesEventsCard()
    {
        ICampPart contribution = new Section();

        var parts = contribution.Parts();

        parts.Should().ContainSingle().Which.Component.Should().Be(typeof(EventsCardViewComponent));
    }
}
