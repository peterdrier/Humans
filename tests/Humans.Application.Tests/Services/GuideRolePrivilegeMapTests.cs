using AwesomeAssertions;
using Humans.Application.Services;
using Humans.Domain.Constants;
using Xunit;

namespace Humans.Application.Tests.Services;

public class GuideRolePrivilegeMapTests
{
    [Theory]
    [InlineData("Camp Admin", RoleNames.CampAdmin)]
    [InlineData("camp admin", RoleNames.CampAdmin)]
    [InlineData("Teams Admin", RoleNames.TeamsAdmin)]
    [InlineData("NoInfo Admin", RoleNames.NoInfoAdmin)]
    [InlineData("Human Admin", RoleNames.HumanAdmin)]
    [InlineData("Finance Admin", RoleNames.FinanceAdmin)]
    [InlineData("Feedback Admin", RoleNames.FeedbackAdmin)]
    [InlineData("Ticket Admin", RoleNames.TicketAdmin)]
    [InlineData("Consent Coordinator", RoleNames.ConsentCoordinator)]
    [InlineData("Volunteer Coordinator", RoleNames.VolunteerCoordinator)]
    public void TryResolve_KnownDisplayName_ReturnsSystemRole(string displayName, string expectedRole)
    {
        GuideRolePrivilegeMap.TryResolve(displayName, out var role).Should().BeTrue();
        role.Should().Be(expectedRole);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unknown")]
    [InlineData("Camp Coordinator")] // A team-coordinator, not a system role
    public void TryResolve_UnknownOrEmpty_ReturnsFalse(string input)
    {
        GuideRolePrivilegeMap.TryResolve(input, out var role).Should().BeFalse();
        role.Should().BeNull();
    }

    [Fact]
    public void ParseParenthetical_MultipleCommaSeparated_ReturnsAll()
    {
        var result = GuideRolePrivilegeMap.ParseParenthetical("Camp Admin, Finance Admin");

        result.Should().BeEquivalentTo([RoleNames.CampAdmin, RoleNames.FinanceAdmin]);
    }

    [Fact]
    public void ParseParenthetical_UnknownTokensDropped()
    {
        var result = GuideRolePrivilegeMap.ParseParenthetical("Camp Admin, Gibberish");

        result.Should().BeEquivalentTo([RoleNames.CampAdmin]);
    }

    [Fact]
    public void ParseParenthetical_NullOrEmpty_ReturnsEmpty()
    {
        GuideRolePrivilegeMap.ParseParenthetical(null).Should().BeEmpty();
        GuideRolePrivilegeMap.ParseParenthetical("").Should().BeEmpty();
        GuideRolePrivilegeMap.ParseParenthetical("   ").Should().BeEmpty();
    }
}
