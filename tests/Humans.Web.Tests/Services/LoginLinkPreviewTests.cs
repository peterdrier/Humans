using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Web.Services;
using Xunit;

namespace Humans.Web.Tests.Services;

/// <summary>
/// <see cref="LoginLinkPreview"/> maps a login ReturnUrl to the nav label of the section that
/// owns it, so a shared deep link previews as that section rather than "Sign In".
/// </summary>
public class LoginLinkPreviewTests
{
    private static readonly MemberNavItem[] Nav =
    [
        new("Nav_Shifts", Controller: "Shifts", Action: "Index"),
        new("Nav_Teams", Controller: "Team", Action: "Index"),
        new("Nav_More", Children:
        [
            new("Nav_Votes", Controller: "GovernanceVotes", Action: "Index"),
            new("divider", Divider: true),
            new("Nav_Guide", RawHref: "/Guide"),
        ]),
    ];

    private static string? UrlFor(string controller, string action) => controller switch
    {
        "Shifts" => "/Shifts",
        "Team" => "/Teams",
        "GovernanceVotes" => "/Governance/Votes",
        _ => null,
    };

    [HumansTheory]
    [InlineData("/Shifts", "Nav_Shifts")]
    [InlineData("/shifts/42?tab=mine", "Nav_Shifts")]
    [InlineData("/Teams/gate#roster", "Nav_Teams")]
    [InlineData("/Governance/Votes/7", "Nav_Votes")]
    [InlineData("/Guide/consents", "Nav_Guide")]
    public void NamesTheSectionWhoseNavLinkSharesTheFirstSegment(string returnUrl, string expected)
    {
        LoginLinkPreview.SectionLabelFor(returnUrl, Nav, UrlFor).Should().Be(expected);
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/Admin/Users")]
    [InlineData("Shifts")]
    [InlineData("//evil.example/Shifts")]
    [InlineData("https://evil.example/Shifts")]
    public void ReturnsNullForUnknownOrNonLocalTargets(string? returnUrl)
    {
        LoginLinkPreview.SectionLabelFor(returnUrl, Nav, UrlFor).Should().BeNull();
    }
}
