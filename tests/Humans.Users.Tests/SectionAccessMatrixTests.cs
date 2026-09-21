using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Users.Tests;

/// <summary>
/// The help widget and the agent's preload both read the Profile access matrix through the
/// <c>ISectionAccessMatrix</c> seam, so the rows are pinned here rather than in Base.
/// </summary>
public class SectionAccessMatrixTests
{
    private static AccessMatrixData Matrix() =>
        new SectionAccessMatrix().AccessMatrices.Should().ContainSingle().Subject;

    [HumansFact]
    public void Contributes_the_Profile_matrix_under_the_key_the_page_calls_it_with()
    {
        var matrix = Matrix();

        matrix.Key.Should().Be("Profile");
        matrix.SectionName.Should().Be("Profile");
        matrix.Order.Should().Be(80);
        matrix.Roles.Should().Equal("Volunteer", "Coordinator", "Board", "HumanAdmin", "Admin");
    }

    /// <summary>
    /// Contact fields are Limited for a Volunteer and for a Coordinator — the table's whole
    /// point is that neither sees them in full, so it is pinned literally.
    /// </summary>
    [HumansFact]
    public void Pins_every_feature_row()
    {
        Matrix().Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "View own profile: Volunteer=Allowed, Coordinator=Allowed, Board=Allowed, HumanAdmin=Allowed, Admin=Allowed",
                "Edit own profile: Volunteer=Allowed, Coordinator=Allowed, Board=Allowed, HumanAdmin=Allowed, Admin=Allowed",
                "View other profiles: Volunteer=Limited, Coordinator=Allowed, Board=Allowed, HumanAdmin=Allowed, Admin=Allowed",
                "View contact fields: Volunteer=Limited, Coordinator=Limited, Board=Allowed, HumanAdmin=Allowed, Admin=Allowed",
                "Admin view of profile: Volunteer=Denied, Coordinator=Denied, Board=Allowed, HumanAdmin=Allowed, Admin=Allowed");
    }
}
