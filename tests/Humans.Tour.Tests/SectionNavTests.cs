using System.Security.Claims;
using AwesomeAssertions;
using NSubstitute;

namespace Humans.Tour.Tests;

/// <summary>The top-nav link exists for visitors only; members get the dashboard card.</summary>
public class SectionNavTests
{
    private static readonly IServiceProvider Services = Substitute.For<IServiceProvider>();

    [HumansFact]
    public void Contributes_one_link_to_the_Tour_page()
    {
        var item = new SectionNav().Items().Should().ContainSingle().Subject;

        item.Controller.Should().Be("Tour");
        item.Action.Should().Be("Index");
    }

    [HumansFact]
    public void Link_is_visible_to_anonymous_visitors()
    {
        var item = new SectionNav().Items().Single();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        item.Visible!(Services, anonymous).Should().BeTrue();
    }

    [HumansFact]
    public void Link_is_hidden_from_signed_in_members()
    {
        var item = new SectionNav().Items().Single();
        var member = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "member")], "test"));

        item.Visible!(Services, member).Should().BeFalse(because: "the signed-in nav is too busy for a Tour slot");
    }
}
