using System.Security.Claims;
using AwesomeAssertions;
using NSubstitute;

namespace Humans.Search.Tests;

/// <summary>
/// The nav item gates on authentication alone — no <c>AppAccess</c> policy — so a signed-in
/// human with no profile yet can still search.
/// </summary>
public sealed class SectionNavTests
{
    [HumansFact]
    public void TheSearchItem_ShowsToAnAuthenticatedUser_WithNoProfileOrPolicy()
    {
        var item = new SectionNav().Items().Should().ContainSingle().Subject;
        var signedIn = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test"));

        item.Policy.Should().BeNull();
        item.Visible!(Substitute.For<IServiceProvider>(), signedIn).Should().BeTrue();
    }

    [HumansFact]
    public void TheSearchItem_IsHiddenFromAnAnonymousVisitor()
    {
        var item = new SectionNav().Items().Single();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        item.Visible!(Substitute.For<IServiceProvider>(), anonymous).Should().BeFalse();
    }
}
