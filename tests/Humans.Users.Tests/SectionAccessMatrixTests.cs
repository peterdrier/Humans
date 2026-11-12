using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Users.Tests;

public class SectionAccessMatrixTests
{
    private static AccessMatrixData Matrix() =>
        ((ISectionAccessMatrix)new Section()).AccessMatrices.Single(m => string.Equals(m.Key, "Profile", StringComparison.Ordinal));

    [HumansFact]
    public void Contributes_the_Profile_matrix_under_the_key_the_page_calls_it_with()
    {
        var matrix = Matrix();

        matrix.Key.Should().Be("Profile");
        matrix.SectionName.Should().Be("Profile");
        matrix.Order.Should().Be(80);
        matrix.Roles.Should().Equal("Volunteer", "Coordinator", "Board", "UsersAdmin", "Admin");
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
                "View own profile: Volunteer=Allowed, Coordinator=Allowed, Board=Allowed, UsersAdmin=Allowed, Admin=Allowed",
                "Edit own profile: Volunteer=Allowed, Coordinator=Allowed, Board=Allowed, UsersAdmin=Allowed, Admin=Allowed",
                "View other profiles: Volunteer=Limited, Coordinator=Allowed, Board=Allowed, UsersAdmin=Allowed, Admin=Allowed",
                "View contact fields: Volunteer=Limited, Coordinator=Limited, Board=Allowed, UsersAdmin=Allowed, Admin=Allowed",
                "Admin view of profile: Volunteer=Denied, Coordinator=Denied, Board=Allowed, UsersAdmin=Allowed, Admin=Allowed");
    }

    [HumansFact]
    public void Pins_the_UsersAdmin_matrix()
    {
        var matrix = ((ISectionAccessMatrix)new Section()).AccessMatrices
            .Single(m => string.Equals(m.Key, "UsersAdmin", StringComparison.Ordinal));

        (matrix.SectionName, matrix.Order).Should().Be(("All Humans", 90));
        matrix.Roles.Should().Equal("Board", "UsersAdmin", "Admin");
        matrix.Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "All humans list: Board=Allowed, UsersAdmin=Allowed, Admin=Allowed");
    }
}
