using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Issues.Domain;
using Humans.Issues.Contracts;
using Xunit;

namespace Humans.Issues.Tests.Domain;

public class IssueStatusTransitionTests
{
    [HumansFact]
    public void IsTerminal_returns_correct_value()
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
    public void RolesFor_unknown_section_returns_empty()
    {
        _routing.RolesFor(null).Should().BeEmpty();
        _routing.RolesFor("UnknownSection").Should().BeEmpty();
    }

    /// <summary>
    /// Profiles and Legal name sections that no longer exist, so nobody declares those queues —
    /// the rows still carrying those strings keep falling through to the Admin-only queue rather
    /// than becoming unroutable (PR peterdrier/Humans#1762).
    /// </summary>
    [HumansTheory]
    [InlineData("Profiles")]
    [InlineData("Legal")]
    [InlineData("ZSomethingElse")]
    public void Unclaimed_section_keys_fall_through_to_admin_only(string section)
    {
        _routing.RolesFor(section).Should().BeEmpty();
        _routing.Resolve(section).Should().BeNull();
        _routing.AllKnownSections.Should().NotContain(section);
        _routing.CanHandle(section, [RoleNames.HumanAdmin]).Should().BeFalse();
        _routing.CanHandle(section, [RoleNames.Admin]).Should().BeTrue();
    }

    /// <summary>The table is whatever DI discovered — no section declares, no queue routes.</summary>
    [HumansFact]
    public void Routing_is_built_from_the_contributions()
    {
        var routing = TestIssueQueues.Routing(
            new TestQueueOwner("Elsewhere", [RoleNames.TeamsAdmin]));

        routing.AllKnownSections.Should().Equal("Elsewhere");
        routing.RolesFor("Elsewhere").Should().Equal(RoleNames.TeamsAdmin);
        routing.Resolve("elsewhere").Should().Be("Elsewhere");
        routing.RolesFor("Tickets").Should().BeEmpty();

        TestIssueQueues.Routing().AllKnownSections.Should().BeEmpty();
    }

    [HumansFact]
    public void RolesFor_known_section_includes_owner_role()
    {
        _routing.RolesFor("Tickets")
            .Should().Contain(RoleNames.TicketAdmin);

        _routing.RolesFor("Scanner")
            .Should().Contain([RoleNames.TicketAdmin, RoleNames.Board]);
    }

    [HumansFact]
    public void SectionsForRoles_returns_sections_user_can_handle()
    {
        var sections = _routing.SectionsForRoles([RoleNames.CampAdmin]);

        sections.Should().Contain("Camps");
        sections.Should().Contain("CityPlanning");
        sections.Should().NotContain("Tickets");

        _routing.SectionsForRoles([RoleNames.TicketAdmin])
            .Should().Contain(["Tickets", "Scanner"]);
    }

    [HumansFact]
    public void SectionsForRoles_empty_role_set_returns_empty()
    {
        var sections = _routing.SectionsForRoles([]);
        sections.Should().BeEmpty();
    }
}
