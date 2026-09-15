using System.Security.Claims;
using AwesomeAssertions;
using NSubstitute;

namespace Humans.Search.Tests;

/// <summary>
/// The nav item's <c>Visible</c> predicate gates on authentication alone, so a signed-in
/// human with no profile yet can still search. How the Shell combines <c>Visible</c> with
/// <c>Policy</c> is pinned in <c>Humans.Web.Tests</c> (<c>SectionSeamTests</c>).
/// </summary>
public sealed class SectionNavTests
{
    [HumansFact]
    public void TheSearchItem_ShowsToAnAuthenticatedUser_WithNoProfile()
    {
        var item = new SectionNav().Items().Should().ContainSingle().Subject;
        var signedIn = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test"));

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
