using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Teams.Tests;

/// <summary>
/// The help widget and the agent's preload both read this section's access matrix through the
/// <c>ISectionAccessMatrix</c> seam, so the rows are pinned here rather than in Base.
/// </summary>
public class SectionAccessMatrixTests
{
    private static AccessMatrixData Matrix() =>
        new SectionAccessMatrix().AccessMatrices.Should().ContainSingle().Subject;

    [HumansFact]
    public void Contributes_the_Teams_matrix_under_the_key_the_page_calls_it_with()
    {
        var matrix = Matrix();

        matrix.Key.Should().Be("Teams");
        matrix.SectionName.Should().Be("Teams");
        matrix.Order.Should().Be(20);
        matrix.Roles.Should().Equal("Volunteer", "Coordinator", "Board", "TeamsAdmin");
    }

    [HumansFact]
    public void Pins_every_feature_row()
    {
        Matrix().Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "View teams & join: Volunteer=Allowed, Coordinator=Allowed, Board=Allowed, TeamsAdmin=Allowed",
                "View team details: Volunteer=Allowed, Coordinator=Allowed, Board=Allowed, TeamsAdmin=Allowed",
                "Manage members: Volunteer=Denied, Coordinator=Allowed, Board=Allowed, TeamsAdmin=Allowed",
                "Manage roles: Volunteer=Denied, Coordinator=Allowed, Board=Allowed, TeamsAdmin=Allowed",
                "Create teams: Volunteer=Denied, Coordinator=Denied, Board=Allowed, TeamsAdmin=Allowed",
                "Delete teams: Volunteer=Denied, Coordinator=Denied, Board=Allowed, TeamsAdmin=Denied",
                "Google resource sync: Volunteer=Denied, Coordinator=Denied, Board=Denied, TeamsAdmin=Limited");
    }
}
