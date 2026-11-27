using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.Camps.Contracts;
using Humans.Tickets.Contracts;
using Humans.Consent.Contracts;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Issues.Domain;
using Humans.Issues.Contracts;
using Xunit;

namespace Humans.Issues.Tests.Domain;

public class IssueStatusTransitionTests
{
    [HumansFact]
    public void IsTerminal_EachStatus_MatchesRule()
    {
        IssueStatus.Triage.IsTerminal().Should().BeFalse();
        IssueStatus.Open.IsTerminal().Should().BeFalse();
        IssueStatus.InProgress.IsTerminal().Should().BeFalse();
        IssueStatus.Resolved.IsTerminal().Should().BeTrue();
        IssueStatus.WontFix.IsTerminal().Should().BeTrue();
        IssueStatus.Duplicate.IsTerminal().Should().BeTrue();
    }

    private readonly IssueSectionRouting _routing = TestIssueQueues.Shipped();

    [HumansFact]
    public void RolesFor_UnknownSection_ReturnsEmpty()
    {
        _routing.RolesFor(null).Should().BeEmpty();
        _routing.RolesFor("UnknownSection").Should().BeEmpty();
    }

    /// <summary>
    /// A key no section declares keeps falling through to the Admin-only queue rather than
    /// becoming unroutable.
    /// </summary>
    [HumansTheory]
    [InlineData("ZSomethingElse")]
    [InlineData("Camping")]
    public void Routing_UnclaimedSectionKey_IsAdminOnly(string section)
    {
        _routing.RolesFor(section).Should().BeEmpty();
        _routing.Resolve(section).Should().BeNull();
        _routing.AllKnownSections.Should().NotContain(section);
        _routing.CanHandle(section, [UsersRoles.UsersAdmin]).Should().BeFalse();
        _routing.CanHandle(section, [RoleNames.Admin]).Should().BeTrue();
    }

    /// <summary>The table is whatever DI discovered — no section declares, no queue routes.</summary>
    [HumansFact]
    public void Routing_Contributions_BuildTheTable()
    {
        var routing = TestIssueQueues.Routing(
            new TestQueueOwner("Elsewhere", [TeamsRoles.TeamsAdmin]));

        routing.AllKnownSections.Should().Equal("Elsewhere");
        routing.RolesFor("Elsewhere").Should().Equal(TeamsRoles.TeamsAdmin);
        routing.Resolve("elsewhere").Should().Be("Elsewhere");
        routing.RolesFor("Tickets").Should().BeEmpty();

        TestIssueQueues.Routing().AllKnownSections.Should().BeEmpty();
    }

    /// <summary>
    /// Profiles (merged into Users) and Legal (renamed Consent) are queue keys that outlived
    /// their section names. Users and Consent declare them under the stored spelling, so rows
    /// filed before those renames keep reaching their handlers.
    /// </summary>
    [HumansTheory]
    [InlineData("Profiles", UsersRoles.UsersAdmin)]
    [InlineData("Legal", ConsentRoles.ConsentAdmin)]
    public void Routing_RenamedSection_KeepsStoredQueueKey(string section, string role)
    {
        _routing.RolesFor(section).Should().Equal(role);
        _routing.Resolve(section).Should().Be(section);
        _routing.CanHandle(section, [role]).Should().BeTrue();
        _routing.CanHandle(section, [TicketsRoles.TicketsAdmin]).Should().BeFalse();
    }

    [HumansFact]
    public void RolesFor_KnownSection_IncludesOwnerRole()
    {
        _routing.RolesFor("Tickets")
            .Should().Contain(TicketsRoles.TicketsAdmin);

        _routing.RolesFor("Scanner")
            .Should().Contain([TicketsRoles.TicketsAdmin, RoleNames.Board]);
    }

    [HumansFact]
    public void SectionsForRoles_WithRoles_ReturnsHandledSections()
    {
        var sections = _routing.SectionsForRoles([CampsRoles.CampsAdmin]);

        sections.Should().Contain("Camps");
        sections.Should().Contain("CityPlanning");
        sections.Should().NotContain("Tickets");

        _routing.SectionsForRoles([TicketsRoles.TicketsAdmin])
            .Should().Contain(["Tickets", "Scanner"]);
    }

    [HumansFact]
    public void SectionsForRoles_NoRoles_ReturnsEmpty()
    {
        var sections = _routing.SectionsForRoles([]);
        sections.Should().BeEmpty();
    }
}
