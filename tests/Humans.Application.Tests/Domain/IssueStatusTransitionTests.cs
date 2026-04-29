using AwesomeAssertions;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Xunit;

namespace Humans.Application.Tests.Domain;

public class IssueStatusTransitionTests
{
    [HumansTheory]
    [InlineData(IssueStatus.Triage, false)]
    [InlineData(IssueStatus.Open, false)]
    [InlineData(IssueStatus.InProgress, false)]
    [InlineData(IssueStatus.Resolved, true)]
    [InlineData(IssueStatus.WontFix, true)]
    [InlineData(IssueStatus.Duplicate, true)]
    public void IsTerminal_returns_correct_value(IssueStatus s, bool expected)
    {
        s.IsTerminal().Should().Be(expected);
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
    }

    [HumansFact]
    public void SectionsForRoles_returns_sections_user_can_handle()
    {
        var sections = IssueSectionRouting.SectionsForRoles([RoleNames.CampAdmin]);

        sections.Should().Contain(IssueSectionRouting.Camps);
        sections.Should().Contain(IssueSectionRouting.CityPlanning);
        sections.Should().NotContain(IssueSectionRouting.Tickets);
    }

    [HumansFact]
    public void SectionsForRoles_empty_role_set_returns_empty()
    {
        var sections = IssueSectionRouting.SectionsForRoles([]);
        sections.Should().BeEmpty();
    }
}
