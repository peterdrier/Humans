using AwesomeAssertions;
using Humans.Application.Models;
using Humans.Application.Services;
using Humans.Domain.Constants;
using Humans.Testing;
using Xunit;

namespace Humans.Application.Tests.Services;

public class GuideFilterTests
{
    private const string Sample = """
        <p>Intro, always visible.</p>
        <div data-guide-role="volunteer" data-guide-roles="">
          <h2>As a Volunteer</h2>
          <p>Volunteer content.</p>
        </div>
        <div data-guide-role="coordinator" data-guide-roles="ConsentCoordinator">
          <h2>As a Coordinator (Consent Coordinator)</h2>
          <p>Coord content.</p>
        </div>
        <div data-guide-role="boardadmin" data-guide-roles="TeamsAdmin">
          <h2>As a Board member / Admin (Teams Admin)</h2>
          <p>Teams admin content.</p>
        </div>
        <h2>Related sections</h2>
        <p>Always visible.</p>
        """;

    private static GuideRoleContext Roles(bool isCoord, params string[] systemRoles) =>
        new(IsAuthenticated: true, IsTeamCoordinator: isCoord,
            SystemRoles: new HashSet<string>(systemRoles, StringComparer.Ordinal));

    [HumansFact]
    public void Apply_Anonymous_KeepsOnlyVolunteerBlock()
    {
        var result = GuideFilter.Apply(Sample, GuideRoleContext.Anonymous);

        result.Should().Contain("Volunteer content.");
        result.Should().Contain("Intro, always visible.");
        result.Should().Contain("Related sections");
        result.Should().NotContain("Coord content.");
        result.Should().NotContain("Teams admin content.");
    }

    [HumansFact]
    public void Apply_PlainVolunteer_SameAsAnonymous()
    {
        var result = GuideFilter.Apply(Sample, Roles(isCoord: false));

        result.Should().Contain("Volunteer content.");
        result.Should().NotContain("Coord content.");
        result.Should().NotContain("Teams admin content.");
    }

    [HumansFact]
    public void Apply_TeamCoordinator_SeesVolunteerAndCoordinator()
    {
        var result = GuideFilter.Apply(Sample, Roles(isCoord: true));

        result.Should().Contain("Volunteer content.");
        result.Should().Contain("Coord content.");
        result.Should().NotContain("Teams admin content.");
    }

    [HumansFact]
    public void Apply_ConsentCoordinatorRoleOnly_SeesCoordinatorBlockByParenthetical()
    {
        var result = GuideFilter.Apply(Sample, Roles(isCoord: false, RoleNames.ConsentCoordinator));

        result.Should().Contain("Coord content.");
        result.Should().NotContain("Teams admin content.");
    }

    [HumansFact]
    public void Apply_ConsentCoordinatorOnBareCoordinatorHeading_NotVisible()
    {
        const string bareCoord = """
            <div data-guide-role="coordinator" data-guide-roles="">
              <h2>As a Coordinator</h2>
              <p>Bare coord content.</p>
            </div>
            """;

        var result = GuideFilter.Apply(bareCoord, Roles(isCoord: false, RoleNames.ConsentCoordinator));

        result.Should().NotContain("Bare coord content.");
    }

    [HumansFact]
    public void Apply_TeamsAdmin_SeesCoordinatorAndBoardOnTeamsFile()
    {
        // Within-file superset: seeing Board/Admin via (Teams Admin) implies seeing Coordinator too.
        var result = GuideFilter.Apply(Sample, Roles(isCoord: false, RoleNames.TeamsAdmin));

        result.Should().Contain("Coord content.");
        result.Should().Contain("Teams admin content.");
    }

    [HumansFact]
    public void Apply_TeamsAdminOnTicketsFile_SeesNothingBeyondVolunteer()
    {
        const string ticketsLike = """
            <div data-guide-role="volunteer" data-guide-roles="">V</div>
            <div data-guide-role="coordinator" data-guide-roles="">C</div>
            <div data-guide-role="boardadmin" data-guide-roles="TicketAdmin">BA</div>
            """;

        var result = GuideFilter.Apply(ticketsLike, Roles(isCoord: false, RoleNames.TeamsAdmin));

        result.Should().Contain("V");
        result.Should().NotContain(">C<");
        result.Should().NotContain("BA");
    }

    [HumansFact]
    public void Apply_Admin_SeesEverything()
    {
        var result = GuideFilter.Apply(Sample, Roles(isCoord: false, RoleNames.Admin));

        result.Should().Contain("Volunteer content.");
        result.Should().Contain("Coord content.");
        result.Should().Contain("Teams admin content.");
    }

    [HumansFact]
    public void Apply_Board_SeesAllBoardAdminBlocksRegardlessOfParenthetical()
    {
        const string mixed = """
            <div data-guide-role="boardadmin" data-guide-roles="">Plain</div>
            <div data-guide-role="boardadmin" data-guide-roles="CampAdmin">Camp-scoped</div>
            """;

        var result = GuideFilter.Apply(mixed, Roles(isCoord: false, RoleNames.Board));

        result.Should().Contain("Plain");
        result.Should().Contain("Camp-scoped");
    }

    [HumansFact]
    public void Apply_NoRoleDivs_ReturnsUnchanged()
    {
        const string plain = "<p>Glossary entries.</p>";

        var result = GuideFilter.Apply(plain, GuideRoleContext.Anonymous);

        result.Should().Be(plain);
    }
}
