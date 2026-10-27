using AwesomeAssertions;
using Humans.Guide.Services;
using Xunit;

namespace Humans.Guide.Tests.Services;

public class GuideRolePrivilegeMapTests
{
    private static readonly GuideRolePrivilegeMap Map = GuideTestRoles.Map;

    [HumansTheory]
    [InlineData("Camps Admin", GuideTestRoles.CampsAdmin)]
    [InlineData("camps admin", GuideTestRoles.CampsAdmin)]
    [InlineData("Teams Admin", GuideTestRoles.TeamsAdmin)]
    [InlineData("Shifts NoInfo", GuideTestRoles.ShiftsNoInfo)]
    [InlineData("Users Admin", GuideTestRoles.UsersAdmin)]
    [InlineData("Finance Admin", GuideTestRoles.FinanceAdmin)]
    [InlineData("Events Admin", GuideTestRoles.EventsAdmin)]
    [InlineData("Store Admin", GuideTestRoles.StoreAdmin)]
    [InlineData("Tickets Admin", GuideTestRoles.TicketsAdmin)]
    [InlineData("Consent Coordinator", GuideTestRoles.ConsentCoordinator)]
    [InlineData("Shifts Admin", GuideTestRoles.ShiftsAdmin)]
    [InlineData("Camp Lead", GuideRolePrivilegeMap.CampLead)]
    public void TryResolve_KnownTitle_ReturnsSystemRole(string displayName, string expectedRole)
    {
        Map.TryResolve(displayName, out var role).Should().BeTrue();
        role.Should().Be(expectedRole);
    }

    [HumansTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unknown")]
    [InlineData("Camp Admin")] // The pre-rename title: only catalog titles resolve
    [InlineData("Camp Coordinator")] // Not a thing: the domain term is Camp Lead
    public void TryResolve_UnknownOrEmpty_ReturnsFalse(string input)
    {
        Map.TryResolve(input, out var role).Should().BeFalse();
        role.Should().BeNull();
    }

    [HumansFact]
    public void MappedRoles_ExcludesCampLead()
    {
        // MappedRoles is the resolver's IsInRole probe list. Camp lead is a per-camp role,
        // never a claim, so probing it would be dead work and imply a system role exists.
        Map.MappedRoles.Should().NotContain(GuideRolePrivilegeMap.CampLead);
        Map.MappedRoles.Should().Contain(GuideTestRoles.CampsAdmin);
    }

    [HumansFact]
    public void ParseParenthetical_MultipleCommaSeparated_ReturnsAll()
    {
        var result = Map.ParseParenthetical("Camps Admin, Finance Admin");

        result.Should().BeEquivalentTo(GuideTestRoles.CampsAdmin, GuideTestRoles.FinanceAdmin);
    }

    [HumansFact]
    public void ParseParenthetical_UnknownTokensDropped()
    {
        var result = Map.ParseParenthetical("Camps Admin, Gibberish");

        result.Should().BeEquivalentTo(GuideTestRoles.CampsAdmin);
    }

    [HumansFact]
    public void ParseParenthetical_NullOrEmpty_ReturnsEmpty()
    {
        Map.ParseParenthetical(null).Should().BeEmpty();
        Map.ParseParenthetical("").Should().BeEmpty();
        Map.ParseParenthetical("   ").Should().BeEmpty();
    }
}
