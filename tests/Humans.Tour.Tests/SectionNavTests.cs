using Humans.Base.Interfaces;
using System.Security.Claims;
using AwesomeAssertions;
using NSubstitute;

namespace Humans.Tour.Tests;

/// <summary>The top-nav link exists for visitors only; members get the dashboard card.</summary>
public class SectionNavTests
{
    private static readonly IServiceProvider Services = Substitute.For<IServiceProvider>();

    [HumansFact]
    public void Nav_ContributesOneTourLink()
    {
        var item = ((ISectionNav)new Section()).Items().Should().ContainSingle().Subject;

        item.Controller.Should().Be("Tour");
        item.Action.Should().Be("Index");
    }

    [HumansFact]
    public void Link_AnonymousVisitor_IsVisible()
    {
        var item = ((ISectionNav)new Section()).Items().Single();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        item.Visible!(Services, anonymous).Should().BeTrue();
    }

    [HumansFact]
    public void Link_SignedInMember_IsHidden()
    {
        var item = ((ISectionNav)new Section()).Items().Single();
        var member = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "member")], "test"));

        item.Visible!(Services, member).Should().BeFalse(because: "the signed-in nav is too busy for a Tour slot");
    }
}
