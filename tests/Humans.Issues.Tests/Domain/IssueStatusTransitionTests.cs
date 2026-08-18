using AwesomeAssertions;
using Humans.Domain.Constants;
using Humans.Issues.Domain;

namespace Humans.Issues.Tests.Domain;

public class IssueStatusTransitionTests
{
    // One Fact rather than a [Theory]: IssueStatus turned internal at the G5 move, and a
    // public theory method cannot take an internal parameter type (CS0051).
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

    [HumansFact]
    public void RolesFor_unknown_section_returns_empty()
    {
        IssueSectionRouting.RolesFor(null).Should().BeEmpty();
        IssueSectionRouting.RolesFor("UnknownSection").Should().BeEmpty();
    }

    [HumansFact]
    public void RolesFor_known_section_includes_owner_role()
    {
        IssueSectionRouting.RolesFor(IssueSectionRouting.Tickets)
            .Should().Contain(RoleNames.TicketAdmin);

        IssueSectionRouting.RolesFor(IssueSectionRouting.Scanner)
            .Should().Contain([RoleNames.TicketAdmin, RoleNames.Board]);
    }

    [HumansFact]
    public void SectionsForRoles_returns_sections_user_can_handle()
    {
        var sections = IssueSectionRouting.SectionsForRoles([RoleNames.CampAdmin]);

        sections.Should().Contain(IssueSectionRouting.Camps);
        sections.Should().Contain(IssueSectionRouting.CityPlanning);
        sections.Should().NotContain(IssueSectionRouting.Tickets);

        IssueSectionRouting.SectionsForRoles([RoleNames.TicketAdmin])
            .Should().Contain([IssueSectionRouting.Tickets, IssueSectionRouting.Scanner]);
    }

    [HumansFact]
    public void SectionsForRoles_empty_role_set_returns_empty()
    {
        var sections = IssueSectionRouting.SectionsForRoles([]);
        sections.Should().BeEmpty();
    }
}
